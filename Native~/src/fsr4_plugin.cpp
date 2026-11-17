// FSR4UnityPlugin - Unity native plugin (D3D12 + Vulkan) that drives AMD FidelityFX Super Resolution
// (FSR 4 ML path, FSR 3.1 analytical fallback) through the FidelityFX API (SDK 2.4.0 ABI).
//
// Threading model
//   Main thread : FSR4_* query/probe exports, FSR4_AllocContextId.
//   Render thread: OnRenderEvent (dispatch / destroy), device shutdown.
// Per-context ffx state is only touched on the render thread. Cross-thread status goes through g_stateMutex.
//
// NOTE: written against the headers in FidelityFX-SDK-2.4.0 and Unity's IUnityGraphicsD3D12v6. It has NOT been
// run on hardware by the author - treat the first run as a bring-up session (see Documentation~/Building-the-native-plugins.md).

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "IUnityGraphicsVulkan.h"   // pulls in <vulkan/vulkan.h> (vendored under Native~/third_party/vulkan)

// FidelityFX API (include root = <SDK>/Kits/FidelityFX)
#include "api/include/ffx_api.h"
#include "api/include/ffx_api_types.h"
#include "api/include/dx12/ffx_api_dx12.h"
// The DX12 and Vulkan backend headers both typedef PfnFfxResourceAllocatorFunc/DeallocatorFunc with different
// signatures. We never use the allocation-callback structs, so give the Vulkan ones different names.
#define PfnFfxResourceAllocatorFunc   PfnFfxResourceAllocatorFuncVK
#define PfnFfxResourceDeallocatorFunc PfnFfxResourceDeallocatorFuncVK
#include "api/include/vk/ffx_api_vk.h"
#undef PfnFfxResourceAllocatorFunc
#undef PfnFfxResourceDeallocatorFunc
#include "upscalers/include/ffx_upscale.h"

using Microsoft::WRL::ComPtr;

#define FSR4_EXPORT extern "C" __declspec(dllexport)

// ---------------------------------------------------------------------------------------------------------------
// Shared with C# (FSR4Native.cs: DispatchParams). Keep field order / size in sync.
// ---------------------------------------------------------------------------------------------------------------
#pragma pack(push, 8)
struct FSR4DispatchParams
{
    int32_t  contextId;
    uint32_t createFlags;     // FfxApiCreateContextUpscaleFlags
    uint64_t versionId;       // 0 = automatic selection (no override)
    uint32_t maxRenderW, maxRenderH;
    uint32_t displayW, displayH;
    uint32_t renderW, renderH;
    uint32_t dispatchFlags;   // FfxApiDispatchFsrUpscaleFlags
    int32_t  enableSharpening;
    int32_t  reset;
    int32_t  pad0;
    float    jitterX, jitterY;
    float    mvScaleX, mvScaleY;
    float    sharpness;
    float    frameTimeMs;
    float    preExposure;
    float    cameraNear, cameraFar, cameraFovY;
    float    viewSpaceToMeters;
    float    pad1;
    // Optional tuning (FfxApiConfigureUpscaleKey). Bit i of tuningMask set = apply tuning[i] when it changed.
    // 0 velocityFactor, 1 reactivenessScale, 2 shadingChangeScale, 3 accumulationAddedPerFrame, 4 minDisocclusionAccumulation
    int32_t  tuningMask;
    float    tuning[5];
    void*    color;           // ID3D12Resource* or native image (Vulkan)
    void*    depth;
    void*    motionVectors;
    void*    exposure;        // optional (may be null)
    void*    output;
};
#pragma pack(pop)
static_assert(sizeof(FSR4DispatchParams) == 168, "FSR4DispatchParams layout must match C# FSR4DispatchParams");

enum FSR4Event : int
{
    kEventDispatch = 1,
    kEventDestroy  = 2,
};

enum FSR4ContextState : int
{
    kStateUnknown = 0,
    kStateOk      = 1,
    kStateFailed  = -1,
};

// ---------------------------------------------------------------------------------------------------------------
// Globals
// ---------------------------------------------------------------------------------------------------------------
static IUnityInterfaces*        g_unity   = nullptr;
static IUnityGraphics*          g_gfx     = nullptr;
static IUnityGraphicsD3D12v6*   g_d3d12   = nullptr;
static UnityGfxRenderer         g_renderer = kUnityGfxRendererNull;

// Vulkan backend state (render thread; written during device initialisation)
static IUnityGraphicsVulkan*    g_vk            = nullptr;
static VkDeviceContext          g_vkDevCtx{};
static PFN_vkDeviceWaitIdle     g_vkDeviceWaitIdle = nullptr;
static uint64_t                 g_vkCurrentFrame = 0;   // UnityVulkanRecordingState::currentFrameNumber
static uint64_t                 g_vkSafeFrame    = 0;   // ...::safeFrameNumber

static bool IsVulkan() { return g_renderer == kUnityGfxRendererVulkan && g_vk != nullptr; }
static bool IsD3D12()  { return g_renderer == kUnityGfxRendererD3D12  && g_d3d12 != nullptr; }

static HMODULE                  g_loaderModule   = nullptr;
static HMODULE                  g_upscalerModule = nullptr;
static PfnFfxCreateContext      g_ffxCreate   = nullptr;
static PfnFfxDestroyContext     g_ffxDestroy  = nullptr;
static PfnFfxQuery              g_ffxQuery    = nullptr;
static PfnFfxDispatch           g_ffxDispatch = nullptr;
static PfnFfxConfigure          g_ffxConfigure = nullptr;
static bool                     g_loadAttempted = false;
static bool                     g_loadOk        = false;

static std::mutex               g_stateMutex;
static std::string              g_lastMessage;
static std::unordered_map<int, int>         g_contextState;    // contextId -> FSR4ContextState
static std::unordered_map<int, std::string> g_contextVersion;  // contextId -> provider version name
static std::unordered_map<int, uint32_t>    g_contextReturn;   // contextId -> last ffx return code
static std::unordered_map<int, FfxApiEffectMemoryUsage> g_contextMemory;   // contextId -> GPU memory the context uses
static std::atomic<int>         g_nextContextId{1};
static std::atomic<uint64_t>    g_dispatchAttempts{0};   // dispatch events that reached the render thread
static std::atomic<uint64_t>    g_dispatchExecuted{0};   // ...and were submitted to the GPU queue

static void SetMessage(const std::string& s)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_lastMessage = s;
    OutputDebugStringA(("[FSR4] " + s + "\n").c_str());
}

static void FfxMessageCallback(uint32_t type, const wchar_t* message)
{
    char buf[1024] = {};
    WideCharToMultiByte(CP_UTF8, 0, message ? message : L"", -1, buf, (int)sizeof(buf) - 1, nullptr, nullptr);
    SetMessage(std::string(type == FFX_API_MESSAGE_TYPE_ERROR ? "ffx error: " : "ffx warning: ") + buf);
}

// ---------------------------------------------------------------------------------------------------------------
// AMD loader
// ---------------------------------------------------------------------------------------------------------------
static std::wstring GetPluginDirectory()
{
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&GetPluginDirectory), &self);
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(self, path, MAX_PATH);
    std::wstring dir(path);
    size_t slash = dir.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : dir.substr(0, slash);
}

static bool LoadAmdLoader()
{
    // Reached from the main thread (status / probe / version queries) and from the render thread (dispatch).
    static std::mutex loadMutex;
    std::lock_guard<std::mutex> loadLock(loadMutex);
    if (g_loadAttempted)
        return g_loadOk;
    g_loadAttempted = true;

    const std::wstring dir = GetPluginDirectory();

    // Preload the effect DLL so the loader finds it as an already-loaded module (it also searches the exe folder and
    // its own folder, but the plugin folder inside a Unity Editor project is neither).
    const wchar_t* api = IsVulkan() ? L"vk" : L"dx12";
    const std::wstring upscalerPath = dir + L"\\amd_fidelityfx_upscaler_" + api + L".dll";
    const std::wstring loaderPath   = dir + L"\\amd_fidelityfx_loader_" + api + L".dll";

    g_upscalerModule = LoadLibraryExW(upscalerPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_upscalerModule)
        SetMessage(std::string("could not load amd_fidelityfx_upscaler_") + (IsVulkan() ? "vk" : "dx12") + ".dll from the plugin folder (error " + std::to_string(GetLastError()) + ")");

    g_loaderModule = LoadLibraryExW(loaderPath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_loaderModule)
    {
        SetMessage(std::string("could not load amd_fidelityfx_loader_") + (IsVulkan() ? "vk" : "dx12") + ".dll from the plugin folder (error " + std::to_string(GetLastError()) + ")");
        return false;
    }

    g_ffxCreate    = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(g_loaderModule, "ffxCreateContext"));
    g_ffxDestroy   = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(g_loaderModule, "ffxDestroyContext"));
    g_ffxConfigure = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(g_loaderModule, "ffxConfigure"));
    g_ffxQuery     = reinterpret_cast<PfnFfxQuery>(GetProcAddress(g_loaderModule, "ffxQuery"));
    g_ffxDispatch  = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(g_loaderModule, "ffxDispatch"));

    g_loadOk = g_ffxCreate && g_ffxDestroy && g_ffxQuery && g_ffxDispatch;
    if (!g_loadOk)
        SetMessage("the AMD loader DLL is missing one of ffxCreateContext/DestroyContext/Query/Dispatch");
    return g_loadOk;
}

// ---------------------------------------------------------------------------------------------------------------
// GPU helpers
// ---------------------------------------------------------------------------------------------------------------
static ComPtr<ID3D12Fence> g_idleFence;
static UINT64              g_idleFenceValue = 0;
static HANDLE              g_idleEvent      = nullptr;

// Render thread only. Blocks until everything submitted to Unity's graphics queue so far has finished.
static void WaitForGpuIdle()
{
    if (IsVulkan())
    {
        // Callers run inside the destroy/shutdown path (event configured with queue access) so no other thread
        // is submitting to the graphics queue while we wait.
        if (g_vkDeviceWaitIdle && g_vkDevCtx.vkDevice)
            g_vkDeviceWaitIdle(g_vkDevCtx.vkDevice);
        return;
    }
    if (!g_d3d12)
        return;
    ID3D12Device* device = g_d3d12->GetDevice();
    ID3D12CommandQueue* queue = g_d3d12->GetCommandQueue();
    if (!device || !queue)
        return;
    if (!g_idleFence)
        device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_idleFence));
    if (!g_idleEvent)
        g_idleEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!g_idleFence || !g_idleEvent)
        return;

    const UINT64 value = ++g_idleFenceValue;
    if (SUCCEEDED(queue->Signal(g_idleFence.Get(), value)) && g_idleFence->GetCompletedValue() < value)
    {
        g_idleFence->SetEventOnCompletion(value, g_idleEvent);
        WaitForSingleObject(g_idleEvent, 5000);
    }
}

// Ring of command lists used to record FSR work. A slot may be reused once Unity's frame fence passed the value that
// ExecuteCommandList returned for it.
struct CommandSlot
{
    ComPtr<ID3D12CommandAllocator>    allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    UINT64                            fenceValue = 0;
    bool                              closed = true;
};
// One list per dispatch, so this must cover (views per frame) x (frames in flight). It was 6, which made a 4-view split-screen
// reuse a list whose GPU work from the previous frame had not finished and stall the render thread on it.
static constexpr int kSlotCount = 16;
static CommandSlot g_slots[kSlotCount];
static int         g_slotIndex = 0;
static HANDLE      g_slotEvent = nullptr;

static CommandSlot* AcquireSlot(ID3D12Device* device)
{
    CommandSlot& slot = g_slots[g_slotIndex];
    g_slotIndex = (g_slotIndex + 1) % kSlotCount;

    if (!slot.allocator)
    {
        if (FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator))))
            return nullptr;
        if (FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator.Get(), nullptr, IID_PPV_ARGS(&slot.list))))
        {
            slot.allocator.Reset();
            return nullptr;
        }
        slot.list->Close();
        slot.closed = true;
    }

    ID3D12Fence* frameFence = g_d3d12->GetFrameFence();
    if (frameFence && slot.fenceValue != 0 && frameFence->GetCompletedValue() < slot.fenceValue)
    {
        if (!g_slotEvent)
            g_slotEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        frameFence->SetEventOnCompletion(slot.fenceValue, g_slotEvent);
        WaitForSingleObject(g_slotEvent, 5000);
    }

    if (FAILED(slot.allocator->Reset()) || FAILED(slot.list->Reset(slot.allocator.Get(), nullptr)))
        return nullptr;
    slot.closed = false;
    return &slot;
}

// ---------------------------------------------------------------------------------------------------------------
// ffx context management (render thread)
// ---------------------------------------------------------------------------------------------------------------
struct FfxCtx
{
    ffxContext ctx = nullptr;
    uint32_t   createFlags = 0;
    uint64_t   versionId = 0;
    uint32_t   maxRenderW = 0, maxRenderH = 0, displayW = 0, displayH = 0;
    bool       failed = false;       // creation failed with these parameters; don't retry until they change
    float      appliedTuning[5] = { 1.0f, 1.0f, 1.0f, 1.0f / 3.0f, -1.0f / 3.0f };   // SDK defaults
    bool       tuningApplied[5] = {};
};
static std::unordered_map<int, FfxCtx> g_ctx;

static ffxReturnCode_t CreateUpscaleContext(uint32_t flags, uint64_t versionId,
                                            uint32_t maxRenderW, uint32_t maxRenderH,
                                            uint32_t displayW, uint32_t displayH, ffxContext* outCtx)
{
    ffxCreateContextDescUpscale create{};
    create.header.type    = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    create.flags          = flags;
    create.maxRenderSize  = { maxRenderW, maxRenderH };
    create.maxUpscaleSize = { displayW, displayH };
    create.fpMessage      = &FfxMessageCallback;

    ffxCreateBackendDX12Desc backendDx{};
    backendDx.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backendDx.device      = IsD3D12() ? g_d3d12->GetDevice() : nullptr;

    ffxCreateBackendVKDesc backendVk{};
    backendVk.header.type    = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    backendVk.deviceContext  = g_vkDevCtx;

    ffxApiHeader& backendHeader = IsVulkan() ? backendVk.header : backendDx.header;

    ffxCreateContextDescUpscaleVersion apiVersion{};
    apiVersion.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE_VERSION;
    apiVersion.version     = FFX_UPSCALER_VERSION;

    ffxOverrideVersion versionOverride{};
    versionOverride.header.type = FFX_API_DESC_TYPE_OVERRIDE_VERSION;
    versionOverride.versionId   = versionId;

    create.header.pNext     = &backendHeader;
    backendHeader.pNext     = &apiVersion.header;
    apiVersion.header.pNext = versionId != 0 ? &versionOverride.header : nullptr;

    return g_ffxCreate(outCtx, &create.header, nullptr);
}

static void DestroyCtx(int id, FfxCtx& c)
{
    if (c.ctx)
    {
        g_ffxDestroy(&c.ctx, nullptr);
        c.ctx = nullptr;
    }
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_contextState.erase(id);
    g_contextVersion.erase(id);
    g_contextReturn.erase(id);
    g_contextMemory.erase(id);
}

// Vulkan: a replaced context may still be referenced by command buffers that have not finished. Park it until
// Unity reports the frame it was last used in as completed (UnityVulkanRecordingState::safeFrameNumber).
struct RetiredCtx { ffxContext ctx; uint64_t lastFrame; };
static std::vector<RetiredCtx> g_retired;

static void ReclaimRetired(bool force)
{
    for (size_t i = 0; i < g_retired.size();)
    {
        if (force || g_retired[i].lastFrame <= g_vkSafeFrame)
        {
            g_ffxDestroy(&g_retired[i].ctx, nullptr);
            g_retired[i] = g_retired.back();
            g_retired.pop_back();
        }
        else
            ++i;
    }
}

static FfxCtx* EnsureContext(const FSR4DispatchParams& p)
{
    FfxCtx& c = g_ctx[p.contextId];
    const bool same = c.createFlags == p.createFlags && c.versionId == p.versionId &&
                      c.maxRenderW == p.maxRenderW && c.maxRenderH == p.maxRenderH &&
                      c.displayW == p.displayW && c.displayH == p.displayH;

    if (same && c.ctx)
        return &c;
    if (same && c.failed)
        return nullptr;

    if (c.ctx)
    {
        if (IsVulkan())
            g_retired.push_back({ c.ctx, g_vkCurrentFrame });   // reclaimed once the GPU is done with it
        else
        {
            WaitForGpuIdle();                   // history/scratch resources may still be in flight
            g_ffxDestroy(&c.ctx, nullptr);
        }
        c.ctx = nullptr;
    }

    c.createFlags = p.createFlags; c.versionId = p.versionId;
    c.maxRenderW = p.maxRenderW;   c.maxRenderH = p.maxRenderH;
    c.displayW = p.displayW;       c.displayH = p.displayH;
    c.failed = false;

    const ffxReturnCode_t rc = CreateUpscaleContext(p.createFlags, p.versionId,
                                                    p.maxRenderW, p.maxRenderH, p.displayW, p.displayH, &c.ctx);
    if (rc != FFX_API_RETURN_OK || !c.ctx)
    {
        c.ctx = nullptr;
        c.failed = true;
        SetMessage("ffxCreateContext failed, code " + std::to_string(rc));
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_contextState[p.contextId]  = kStateFailed;
        g_contextReturn[p.contextId] = rc;
        return nullptr;
    }

    // Ask which provider we ended up with (e.g. "FSR 4.1.2") for diagnostics.
    std::string versionName;
    {
        ffxQueryGetProviderVersion q{};
        q.header.type = FFX_API_QUERY_DESC_TYPE_GET_PROVIDER_VERSION;
        if (g_ffxQuery(&c.ctx, &q.header) == FFX_API_RETURN_OK && q.versionName)
            versionName = q.versionName;
    }
    FfxApiEffectMemoryUsage mem{};
    {
        ffxQueryDescUpscaleGetGPUMemoryUsage q{};
        q.header.type = FFX_API_QUERY_DESC_TYPE_UPSCALE_GPU_MEMORY_USAGE;
        q.gpuMemoryUsageUpscaler = &mem;
        if (g_ffxQuery(&c.ctx, &q.header) != FFX_API_RETURN_OK)
            mem = {};
    }
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_contextMemory[p.contextId]  = mem;
    g_contextState[p.contextId]   = kStateOk;
    g_contextVersion[p.contextId] = versionName;
    g_contextReturn[p.contextId]  = FFX_API_RETURN_OK;
    return &c;
}

// ---------------------------------------------------------------------------------------------------------------
// Render events
// ---------------------------------------------------------------------------------------------------------------
static constexpr D3D12_RESOURCE_STATES kSrvAll =
    D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;

// Applies changed tuning values through ffxConfigure (render thread). Unsupported keys (e.g. FSR 4 ML ignores some)
// just return an error code, which is reported once and otherwise ignored.
static void ApplyTuning(FfxCtx& c, const FSR4DispatchParams& p)
{
    if (!g_ffxConfigure || p.tuningMask == 0)
        return;
    static const uint64_t keys[5] = {
        FFX_API_CONFIGURE_UPSCALE_KEY_FVELOCITYFACTOR,
        FFX_API_CONFIGURE_UPSCALE_KEY_FREACTIVENESSSCALE,
        FFX_API_CONFIGURE_UPSCALE_KEY_FSHADINGCHANGESCALE,
        FFX_API_CONFIGURE_UPSCALE_KEY_FACCUMULATIONADDEDPERFRAME,
        FFX_API_CONFIGURE_UPSCALE_KEY_FMINDISOCCLUSIONACCUMULATION };
    for (int i = 0; i < 5; ++i)
    {
        if (!(p.tuningMask & (1 << i)))
            continue;
        if (c.tuningApplied[i] && c.appliedTuning[i] == p.tuning[i])
            continue;
        float value = p.tuning[i];
        ffxConfigureDescUpscaleKeyValue kv{};
        kv.header.type = FFX_API_CONFIGURE_DESC_TYPE_UPSCALE_KEYVALUE;
        kv.key = keys[i];
        kv.ptr = &value;
        const ffxReturnCode_t rc = g_ffxConfigure(&c.ctx, &kv.header);
        if (rc != FFX_API_RETURN_OK)
            SetMessage("ffxConfigure(tuning key " + std::to_string(i) + ") not accepted by this provider, code " + std::to_string(rc));
        c.appliedTuning[i] = p.tuning[i];
        c.tuningApplied[i] = true;
    }
}

// Fills the backend-independent part of the dispatch description.
static void FillDispatchCommon(ffxDispatchDescUpscale& d, const FSR4DispatchParams& p)
{
    d.header.type  = FFX_API_DISPATCH_DESC_TYPE_UPSCALE;
    d.jitterOffset     = { p.jitterX, p.jitterY };
    d.motionVectorScale= { p.mvScaleX, p.mvScaleY };
    d.renderSize       = { p.renderW, p.renderH };
    d.upscaleSize      = { p.displayW, p.displayH };
    d.enableSharpening = p.enableSharpening != 0;
    d.sharpness        = p.sharpness;
    d.frameTimeDelta   = p.frameTimeMs;
    d.preExposure      = p.preExposure > 0.0f ? p.preExposure : 1.0f;
    d.reset            = p.reset != 0;
    d.cameraNear       = p.cameraNear;
    d.cameraFar        = p.cameraFar;
    d.cameraFovAngleVertical = p.cameraFovY;
    d.viewSpaceToMetersFactor = p.viewSpaceToMeters > 0.0f ? p.viewSpaceToMeters : 1.0f;
    d.flags            = p.dispatchFlags;
}

// --------------------------------------------- Vulkan ---------------------------------------------------------
// Records straight into Unity's current command buffer. Unity inserts the layout transitions through AccessTexture
// (SHADER_READ_ONLY_OPTIMAL for inputs, GENERAL for the output) and FFX restores the layouts it was told about.
static bool VkAccess(void* native, VkImageLayout layout, VkAccessFlags access, UnityVulkanImage* out)
{
    return g_vk->AccessTexture(native, UnityVulkanWholeImage, layout, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, access,
                               kUnityVulkanResourceAccess_PipelineBarrier, out);
}

static FfxApiResource VkMakeResource(const UnityVulkanImage& img, uint32_t ffxState)
{
    VkImageCreateInfo ci{};
    ci.sType         = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType     = img.type;
    ci.format        = img.format;
    ci.extent        = img.extent;
    ci.mipLevels     = static_cast<uint32_t>(img.mipCount > 0 ? img.mipCount : 1);
    ci.arrayLayers   = static_cast<uint32_t>(img.layers > 0 ? img.layers : 1);
    ci.samples       = img.samples;
    ci.tiling        = img.tiling;
    ci.usage         = img.usage;
    return ffxApiGetResourceVK(reinterpret_cast<void*>(img.image), ffxApiGetImageResourceDescriptionVK(img.image, ci, 0), ffxState);
}

static void DoDispatchVK(const FSR4DispatchParams& p)
{
    // Frame counters first: they decide which retired contexts may be destroyed (and need no resource access).
    UnityVulkanRecordingState rs{};
    if (!g_vk->CommandRecordingState(&rs, kUnityVulkanGraphicsQueueAccess_DontCare))
        return;
    g_vkCurrentFrame = rs.currentFrameNumber;
    g_vkSafeFrame    = rs.safeFrameNumber;
    ReclaimRetired(false);

    FfxCtx* c = EnsureContext(p);
    if (!c)
        return;
    ApplyTuning(*c, p);

    UnityVulkanImage color{}, depth{}, mv{}, exposure{}, output{};
    const VkAccessFlags rd = VK_ACCESS_SHADER_READ_BIT;
    bool ok = VkAccess(p.color,         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, rd, &color) &&
              VkAccess(p.depth,         VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, rd, &depth) &&
              VkAccess(p.motionVectors, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, rd, &mv) &&
              VkAccess(p.output,        VK_IMAGE_LAYOUT_GENERAL, VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT, &output);
    if (ok && p.exposure)
        ok = VkAccess(p.exposure, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, rd, &exposure);
    if (!ok)
    {
        SetMessage("Vulkan: Unity could not provide one of the FSR textures (AccessTexture failed)");
        return;
    }

    // AccessTexture may have recorded barriers - fetch the command buffer afterwards.
    if (!g_vk->CommandRecordingState(&rs, kUnityVulkanGraphicsQueueAccess_DontCare) || !rs.commandBuffer)
        return;

    ffxDispatchDescUpscale d{};
    FillDispatchCommon(d, p);
    d.commandList   = rs.commandBuffer;
    d.color         = VkMakeResource(color,  FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.depth         = VkMakeResource(depth,  FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.motionVectors = VkMakeResource(mv,     FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    if (p.exposure)
        d.exposure  = VkMakeResource(exposure, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.output        = VkMakeResource(output, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);

    const ffxReturnCode_t rc = g_ffxDispatch(&c->ctx, &d.header);
    if (rc != FFX_API_RETURN_OK)
    {
        SetMessage("ffxDispatch (Vulkan) failed, code " + std::to_string(rc));
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_contextReturn[p.contextId] = rc;
        return;
    }
    g_dispatchExecuted.fetch_add(1);
}

// --------------------------------------------- D3D12 ----------------------------------------------------------
static void DoDispatchDX12(const FSR4DispatchParams& p)
{
    ID3D12Device* device = g_d3d12->GetDevice();
    FfxCtx* c = EnsureContext(p);
    if (!c)
        return;
    ApplyTuning(*c, p);

    CommandSlot* slot = AcquireSlot(device);
    if (!slot)
    {
        SetMessage("could not acquire a command list");
        return;
    }

    ffxDispatchDescUpscale d{};
    FillDispatchCommon(d, p);
    d.commandList  = slot->list.Get();
    d.color        = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.color),         FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.depth        = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.depth),         FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.motionVectors= ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.motionVectors), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    if (p.exposure)
        d.exposure = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.exposure),      FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    d.output       = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.output),        FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);

    const ffxReturnCode_t rc = g_ffxDispatch(&c->ctx, &d.header);
    slot->list->Close();
    slot->closed = true;
    if (rc != FFX_API_RETURN_OK)
    {
        SetMessage("ffxDispatch failed, code " + std::to_string(rc));
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_contextReturn[p.contextId] = rc;
        return;     // list is closed and empty-ish; not executing it keeps Unity's state untouched
    }

    // Tell Unity which states the resources are in before / after. FFX restores the states it was given.
    UnityGraphicsD3D12ResourceState states[5];
    int n = 0;
    states[n++] = { static_cast<ID3D12Resource*>(p.color),         kSrvAll, kSrvAll };
    states[n++] = { static_cast<ID3D12Resource*>(p.depth),         kSrvAll, kSrvAll };
    states[n++] = { static_cast<ID3D12Resource*>(p.motionVectors), kSrvAll, kSrvAll };
    if (p.exposure)
        states[n++] = { static_cast<ID3D12Resource*>(p.exposure),  kSrvAll, kSrvAll };
    states[n++] = { static_cast<ID3D12Resource*>(p.output), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };

    slot->fenceValue = g_d3d12->ExecuteCommandList(slot->list.Get(), n, states);
    g_dispatchExecuted.fetch_add(1);
}

static void DoDispatch(const FSR4DispatchParams& p)
{
    g_dispatchAttempts.fetch_add(1);
    if (!(IsD3D12() || IsVulkan()) || !LoadAmdLoader())
        return;
    if (!p.color || !p.depth || !p.motionVectors || !p.output)
        return;
    if (IsVulkan())
        DoDispatchVK(p);
    else
        DoDispatchDX12(p);
}

static void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
{
    switch (eventId)
    {
    case kEventDispatch:
        if (data)
            DoDispatch(*static_cast<const FSR4DispatchParams*>(data));
        break;
    case kEventDestroy:
    {
        const int id = static_cast<int>(reinterpret_cast<intptr_t>(data));
        auto it = g_ctx.find(id);
        if (it != g_ctx.end())
        {
            if (it->second.ctx)
                WaitForGpuIdle();
            DestroyCtx(id, it->second);
            g_ctx.erase(it);
        }
        if (IsVulkan() && !g_retired.empty())
        {
            WaitForGpuIdle();
            ReclaimRetired(true);
        }
        break;
    }
    default:
        break;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Unity plugin lifecycle
// ---------------------------------------------------------------------------------------------------------------
static void ShutdownDevice()
{
    if ((g_d3d12 || g_vk) && (!g_ctx.empty() || !g_retired.empty()))
    {
        WaitForGpuIdle();
        for (auto& kv : g_ctx)
            DestroyCtx(kv.first, kv.second);
        g_ctx.clear();
        ReclaimRetired(true);
    }
    g_vk = nullptr;
    g_vkDeviceWaitIdle = nullptr;
    g_vkDevCtx = {};
    for (auto& s : g_slots)
    {
        s.list.Reset();
        s.allocator.Reset();
        s.fenceValue = 0;
    }
    g_idleFence.Reset();
    g_d3d12 = nullptr;
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
    switch (eventType)
    {
    case kUnityGfxDeviceEventInitialize:
        g_renderer = g_gfx->GetRenderer();
        if (g_renderer == kUnityGfxRendererD3D12)
        {
            g_d3d12 = g_unity->Get<IUnityGraphicsD3D12v6>();
            if (g_d3d12)
            {
                // Make sure earlier command buffers are submitted before we run, and that our work is visible to
                // whatever Unity records next. We record into our own list, so no ModifiesCommandBuffersState.
                UnityD3D12PluginEventConfig cfg{};
                cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
                cfg.flags = kUnityD3D12EventConfigFlag_FlushCommandBuffers | kUnityD3D12EventConfigFlag_SyncWorkerThreads;
                cfg.ensureActiveRenderTextureIsBound = false;
                g_d3d12->ConfigureEvent(kEventDispatch, &cfg);
                g_d3d12->ConfigureEvent(kEventDestroy, &cfg);
            }
        }
        else if (g_renderer == kUnityGfxRendererVulkan)
        {
            g_vk = g_unity->Get<IUnityGraphicsVulkan>();
            if (g_vk)
            {
                const UnityVulkanInstance inst = g_vk->Instance();
                PFN_vkGetDeviceProcAddr getDeviceProc = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
                    inst.getInstanceProcAddr(inst.instance, "vkGetDeviceProcAddr"));
                g_vkDevCtx.vkDevice         = inst.device;
                g_vkDevCtx.vkPhysicalDevice = inst.physicalDevice;
                g_vkDevCtx.vkDeviceProcAddr = getDeviceProc;
                g_vkDeviceWaitIdle = getDeviceProc ? reinterpret_cast<PFN_vkDeviceWaitIdle>(getDeviceProc(inst.device, "vkDeviceWaitIdle")) : nullptr;

                // Dispatch: record into Unity's command buffer outside of any render pass. FFX rebinds pipelines and
                // descriptor sets, so Unity must treat its cached command-buffer state as dirty afterwards.
                UnityVulkanPluginEventConfig dispatchCfg{};
                dispatchCfg.renderPassPrecondition = kUnityVulkanRenderPass_EnsureOutside;
                dispatchCfg.graphicsQueueAccess    = kUnityVulkanGraphicsQueueAccess_DontCare;
                dispatchCfg.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission |
                                    kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState;
                g_vk->ConfigureEvent(kEventDispatch, &dispatchCfg);

                // Destroy: needs an idle device, so flush Unity's work and keep other threads off the queue.
                UnityVulkanPluginEventConfig destroyCfg{};
                destroyCfg.renderPassPrecondition = kUnityVulkanRenderPass_DontCare;
                destroyCfg.graphicsQueueAccess    = kUnityVulkanGraphicsQueueAccess_Allow;
                destroyCfg.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission |
                                   kUnityVulkanEventConfigFlag_FlushCommandBuffers |
                                   kUnityVulkanEventConfigFlag_SyncWorkerThreads;
                g_vk->ConfigureEvent(kEventDestroy, &destroyCfg);
            }
        }
        break;
    case kUnityGfxDeviceEventShutdown:
        ShutdownDevice();
        g_renderer = kUnityGfxRendererNull;
        break;
    default:
        break;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unity = unityInterfaces;
    g_gfx   = g_unity->Get<IUnityGraphics>();
    g_gfx->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);   // plugin may be loaded after device creation
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_gfx)
        g_gfx->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    ShutdownDevice();
}

// ---------------------------------------------------------------------------------------------------------------
// Exports (C# P/Invoke)
// ---------------------------------------------------------------------------------------------------------------
FSR4_EXPORT UnityRenderingEventAndData FSR4_GetRenderEventFunc()
{
    return OnRenderEvent;
}

FSR4_EXPORT int FSR4_AllocContextId()
{
    return g_nextContextId.fetch_add(1);
}

// 0 = ok, 1 = not running on D3D12 or Vulkan, 2 = AMD DLLs could not be loaded
FSR4_EXPORT int FSR4_GetStatus()
{
    if (!IsD3D12() && !IsVulkan())
        return 1;
    return LoadAmdLoader() ? 0 : 2;
}

FSR4_EXPORT void FSR4_GetDispatchCounts(uint64_t* attempts, uint64_t* executed)
{
    if (attempts) *attempts = g_dispatchAttempts.load();
    if (executed) *executed = g_dispatchExecuted.load();
}

FSR4_EXPORT int FSR4_GetStructSize()
{
    return static_cast<int>(sizeof(FSR4DispatchParams));
}

FSR4_EXPORT int FSR4_GetLastMessage(char* buffer, int bufferLength)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    if (!buffer || bufferLength <= 0)
        return static_cast<int>(g_lastMessage.size());
    strncpy_s(buffer, static_cast<size_t>(bufferLength), g_lastMessage.c_str(), _TRUNCATE);
    return static_cast<int>(g_lastMessage.size());
}

// Returns the number of versions available (names/ids filled up to maxCount). Version names are copied out because
// the SDK may overwrite its internal strings on the next query. ids[i] / names[i * nameLength] belong together.
FSR4_EXPORT int FSR4_GetVersions(uint64_t* ids, char* names, int maxCount, int nameLength)
{
    if (!(IsD3D12() || IsVulkan()) || !LoadAmdLoader())
        return 0;
    void* device = IsVulkan() ? static_cast<void*>(&g_vkDevCtx) : static_cast<void*>(g_d3d12->GetDevice());
    if (!device)
        return 0;

    uint64_t count = 0;
    ffxQueryDescGetVersions q{};
    q.header.type      = FFX_API_QUERY_DESC_TYPE_GET_VERSIONS;
    q.createDescType   = FFX_API_CREATE_CONTEXT_DESC_TYPE_UPSCALE;
    q.device           = device;
    q.outputCount      = &count;
    if (g_ffxQuery(nullptr, &q.header) != FFX_API_RETURN_OK || count == 0)
        return 0;

    std::vector<uint64_t>    idArr(static_cast<size_t>(count));
    std::vector<const char*> nameArr(static_cast<size_t>(count), nullptr);
    q.versionIds   = idArr.data();
    q.versionNames = nameArr.data();
    if (g_ffxQuery(nullptr, &q.header) != FFX_API_RETURN_OK)
        return 0;

    const int total = static_cast<int>(count);
    for (int i = 0; i < total && i < maxCount; ++i)
    {
        if (ids)
            ids[i] = idArr[static_cast<size_t>(i)];
        if (names && nameLength > 0)
            strncpy_s(names + static_cast<size_t>(i) * nameLength, static_cast<size_t>(nameLength),
                      nameArr[static_cast<size_t>(i)] ? nameArr[static_cast<size_t>(i)] : "", _TRUNCATE);
    }
    return total;
}

// Creates and immediately destroys a tiny upscaler context so the main thread learns synchronously whether the
// requested configuration works on this machine. Returns the ffx return code (0 = OK), or -1 if unavailable.
FSR4_EXPORT int FSR4_Probe(uint32_t createFlags, uint64_t versionId)
{
    if (!(IsD3D12() || IsVulkan()) || !LoadAmdLoader())
        return -1;

    ffxContext ctx = nullptr;
    const ffxReturnCode_t rc = CreateUpscaleContext(createFlags, versionId, 256, 144, 512, 288, &ctx);
    if (rc == FFX_API_RETURN_OK && ctx)
        g_ffxDestroy(&ctx, nullptr);
    else
        SetMessage("probe: ffxCreateContext failed, code " + std::to_string(rc));
    return static_cast<int>(rc);
}

// 1 = context exists and works, -1 = creation failed, 0 = nothing created yet.
FSR4_EXPORT int FSR4_GetContextState(int contextId)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    auto it = g_contextState.find(contextId);
    return it == g_contextState.end() ? kStateUnknown : it->second;
}

// Bytes of GPU memory the upscaler context owns (0 when unknown). aliasable is the part the engine could alias.
FSR4_EXPORT int FSR4_GetContextMemory(int contextId, uint64_t* totalBytes, uint64_t* aliasableBytes)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    auto it = g_contextMemory.find(contextId);
    if (it == g_contextMemory.end())
        return 0;
    if (totalBytes) *totalBytes = it->second.totalUsageInBytes;
    if (aliasableBytes) *aliasableBytes = it->second.aliasableUsageInBytes;
    return 1;
}

// 0 = none, 1 = Direct3D12, 2 = Vulkan - what the plugin attached to.
FSR4_EXPORT int FSR4_GetBackend()
{
    return IsD3D12() ? 1 : (IsVulkan() ? 2 : 0);
}

FSR4_EXPORT int FSR4_GetContextVersion(int contextId, char* buffer, int bufferLength)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    auto it = g_contextVersion.find(contextId);
    if (it == g_contextVersion.end() || !buffer || bufferLength <= 0)
        return 0;
    strncpy_s(buffer, static_cast<size_t>(bufferLength), it->second.c_str(), _TRUNCATE);
    return static_cast<int>(it->second.size());
}
