// FSR4FrameGen - optional Unity native plugin: AMD FidelityFX Frame Generation (frame interpolation) for D3D12 and Vulkan.
//
// EXPERIMENTAL. Written against FidelityFX SDK 2.4.0 and Unity 6.6 plugin headers. It has NOT been run on hardware by
// the author; treat the first run as a bring-up session. It works only in Player builds (the Editor has no swapchain on
// D3D12, and Vulkan needs the swapchain hook installed before the swapchain is created).
//
// Design ("manual interpolation + extra present", no swapchain replacement)
//   * Per frame, inside Unity's render graph (after the upscale pass), the plugin dispatches FFX frame-generation
//     "PrepareV2" with the frame's depth + motion vectors + jitter + camera vectors (event kEventPrepare, render thread).
//   * The plugin hooks the swapchain present (D3D12: IDXGISwapChain::Present/Present1 vtable; Vulkan:
//     vkQueuePresentKHR via IUnityGraphicsVulkan::InterceptVulkanAPI). When Unity presents frame N we
//       1. copy the finished backbuffer into our own texture,
//       2. run ffxDispatch(FrameGeneration) with that copy -> interpolated image (between N-1 and N),
//       3. present the interpolated image first, then
//       4. put frame N back into the next backbuffer and let the real present happen after half a frame interval.
//   * The FFX frame-generation context runs with FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY (the SDK sample's
//     manual mode), so FFX never touches the swapchain itself.
//
// Threading: kEventPrepare runs on Unity's render thread; the present hooks run on whatever thread Unity presents from
// (its submission thread). All ffx calls are guarded by g_fgMutex.

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdarg>
#ifdef _MSC_VER
#include <intrin.h>
#endif
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "IUnityGraphicsVulkan.h"

#include "api/include/ffx_api.h"
#include "api/include/ffx_api_types.h"
#include "api/include/dx12/ffx_api_dx12.h"
#define PfnFfxResourceAllocatorFunc   PfnFfxResourceAllocatorFuncVK
#define PfnFfxResourceDeallocatorFunc PfnFfxResourceDeallocatorFuncVK
#include "api/include/vk/ffx_api_vk.h"
#undef PfnFfxResourceAllocatorFunc
#undef PfnFfxResourceDeallocatorFunc
#include "framegeneration/include/ffx_framegeneration.h"

using Microsoft::WRL::ComPtr;

#define FG_EXPORT extern "C" __declspec(dllexport)

// ---------------------------------------------------------------------------------------------------------------
// Shared with C# (FSR4FrameGenNative.cs). Keep field order / size in sync.
// ---------------------------------------------------------------------------------------------------------------
#pragma pack(push, 8)
struct FGPrepareParams
{
    uint32_t createFlags;                 // FfxApiCreateContextFramegenerationFlags
    uint32_t maxRenderW, maxRenderH;
    uint32_t renderW, renderH;
    uint32_t displayW, displayH;
    int32_t  reset;
    float    jitterX, jitterY;
    float    mvScaleX, mvScaleY;
    float    frameTimeMs;
    float    cameraNear, cameraFar, cameraFovY;
    float    viewSpaceToMeters;
    float    camPos[3], camUp[3], camRight[3], camForward[3];
    int32_t  pad0;
    void*    depth;                       // ID3D12Resource* or native VkImage
    void*    motionVectors;
};
struct FGConfig
{
    int32_t  enabled;
    int32_t  pacing;                      // 0 = off, 1 = pace the real frame half an interval after the generated one
    uint32_t debugFlags;                  // FfxApiDispatchFramegenerationFlags (tear lines, reset indicators, debug view, pacing lines)
    int32_t  transferFunction;            // FfxApiBackbufferTransferFunction
    float    minLuminance, maxLuminance;  // nits (HDR only)
    int32_t  reserved0, reserved1;
};
struct FGStats
{
    int32_t  state;                       // see FGState
    int32_t  backend;                     // 0 none, 1 D3D12, 2 Vulkan
    int32_t  lastReturn;                  // last ffx return code that was not OK
    int32_t  pad;
    uint64_t prepared;                    // prepare dispatches recorded
    uint64_t generated;                   // frames generated + presented
    uint64_t skipped;                     // presents passed through without generating
    uint64_t vramBytes;                   // GPU memory of the ffx context (+ our two textures)
    float    baseFrameMs;                 // smoothed time between real frames
    float    lastHookMs;                  // CPU time spent inside the last generated present
};
#pragma pack(pop)
static_assert(sizeof(FGPrepareParams) == 136, "FGPrepareParams layout must match C#");
static_assert(sizeof(FGConfig) == 32, "FGConfig layout must match C#");
static_assert(sizeof(FGStats) == 56, "FGStats layout must match C#");

enum FGEvent : int { kEventPrepare = 1, kEventDestroy = 2 };

enum FGState : int
{
    kFgIdle = 0,            // nothing happened yet
    kFgActive = 1,          // generating
    kFgUnsupportedApi = 2,  // not D3D12 / Vulkan
    kFgDllMissing = 3,      // AMD loader / frame generation DLL could not be loaded
    kFgNoSwapchain = 4,     // Editor (no swapchain) or Vulkan swapchain created before the hook
    kFgContextFailed = 5,   // ffxCreateContext failed
    kFgUnsupportedFormat = 6,
    kFgStalled = 7,         // the present hook did not keep up; generation was switched off
};

// ---------------------------------------------------------------------------------------------------------------
// Globals
// ---------------------------------------------------------------------------------------------------------------
static IUnityInterfaces*       g_unity = nullptr;
static IUnityGraphics*         g_gfx   = nullptr;
static IUnityGraphicsD3D12v8*  g_d3d12 = nullptr;
static IUnityGraphicsVulkan*   g_vk    = nullptr;
static UnityGfxRenderer        g_renderer = kUnityGfxRendererNull;

static bool IsD3D12()  { return g_renderer == kUnityGfxRendererD3D12 && g_d3d12 != nullptr; }
static bool IsVulkan() { return g_renderer == kUnityGfxRendererVulkan && g_vk != nullptr; }

static std::mutex       g_stateMutex;
static std::string      g_lastMessage;
static std::mutex       g_fgMutex;                    // every ffx call on the FG context
static FGConfig         g_cfg{};                      // guarded by g_stateMutex
static std::atomic<int> g_state{ kFgIdle };
static std::atomic<int> g_lastReturn{ 0 };
static std::atomic<uint64_t> g_statPrepared{ 0 }, g_statGenerated{ 0 }, g_statSkipped{ 0 };
static std::atomic<uint64_t> g_vram{ 0 };
static std::atomic<float>    g_baseFrameMs{ 0.0f }, g_lastHookMs{ 0.0f };

static HMODULE g_loaderModule = nullptr, g_fgModule = nullptr;
static PfnFfxCreateContext  g_ffxCreate = nullptr;
static PfnFfxDestroyContext g_ffxDestroy = nullptr;
static PfnFfxQuery          g_ffxQuery = nullptr;
static PfnFfxDispatch       g_ffxDispatch = nullptr;
static PfnFfxConfigure      g_ffxConfigure = nullptr;
static bool g_loadAttempted = false, g_loadOk = false;

// FG context + the per-frame hand-over between the render thread (prepare) and the present hook.
static ffxContext g_fg = nullptr;
static uint32_t   g_fgCreateFlags = 0, g_fgW = 0, g_fgH = 0, g_fgMaxW = 0, g_fgMaxH = 0, g_fgFormat = 0;
static bool       g_fgFailed = false;
static uint64_t   g_frameId = 0;                       // render thread; ++ per prepared frame
static std::atomic<uint64_t> g_preparedFrame{ 0 };     // last frame id prepared (render thread -> hook)
static std::atomic<uint64_t> g_handledFrame{ 0 };      // last frame id the hook finished (generated or skipped)
static std::atomic<bool>     g_resetNext{ true };
static int        g_stallStrikes = 0;

static void SetMessage(const std::string& s)
{
    { std::lock_guard<std::mutex> lock(g_stateMutex); g_lastMessage = s; }
    OutputDebugStringA(("[FSR4FrameGen] " + s + "\n").c_str());
}

static double NowMs()
{
    static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
    LARGE_INTEGER t; QueryPerformanceCounter(&t);
    return 1000.0 * double(t.QuadPart) / double(freq.QuadPart);
}

// Sleep (coarse) then spin (fine) until the QPC time `targetMs`.
static void PaceUntil(double targetMs)
{
    for (;;)
    {
        const double left = targetMs - NowMs();
        if (left <= 0.0) return;
        if (left > 2.5) Sleep(1); else YieldProcessor();
        if (left > 50.0) return;   // never block the submission thread for long
    }
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
    const size_t slash = dir.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : dir.substr(0, slash);
}

// ---------------------------------------------------------------------------------------------------------------
// Diagnostics: a small log file next to the plugin (flushed per line, capped) so a crash in a hook can be traced.
// ---------------------------------------------------------------------------------------------------------------
static std::mutex g_logMutex;
static FILE*      g_logFile = nullptr;
static int        g_logLines = 0;

static void FgLog(const char* fmt, ...)
{
    std::lock_guard<std::mutex> lock(g_logMutex);
    if (g_logLines >= 600)
        return;
    if (!g_logFile)
    {
        const std::wstring path = GetPluginDirectory() + L"\\FSR4FrameGen.log";
        g_logFile = _wfopen(path.c_str(), L"w");
        if (!g_logFile) { g_logLines = 100000; return; }
    }
    va_list args;
    va_start(args, fmt);
    vfprintf(g_logFile, fmt, args);
    va_end(args);
    fputc('\n', g_logFile);
    fflush(g_logFile);
    ++g_logLines;
}

static std::string ModuleOf(const void* addr)
{
    HMODULE m = nullptr;
    char name[MAX_PATH] = {};
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCSTR>(addr), &m) && m &&
        GetModuleFileNameA(m, name, MAX_PATH))
    {
        const char* slash = strrchr(name, '\\');
        char buf[MAX_PATH + 32];
        snprintf(buf, sizeof(buf), "%s+0x%llx", slash ? slash + 1 : name, (unsigned long long)((const char*)addr - (const char*)m));
        return buf;
    }
    return "?";
}

static bool LoadAmdLoader()
{
    if (g_loadAttempted)
        return g_loadOk;
    g_loadAttempted = true;
    const std::wstring dir = GetPluginDirectory();
    const wchar_t* api = IsVulkan() ? L"vk" : L"dx12";

    g_fgModule = LoadLibraryExW((dir + L"\\amd_fidelityfx_framegeneration_" + api + L".dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_fgModule)
        SetMessage(std::string("could not load amd_fidelityfx_framegeneration_") + (IsVulkan() ? "vk" : "dx12") + ".dll from the plugin folder (error " + std::to_string(GetLastError()) + ")");
    g_loaderModule = LoadLibraryExW((dir + L"\\amd_fidelityfx_loader_" + api + L".dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!g_loaderModule)
    {
        SetMessage(std::string("could not load amd_fidelityfx_loader_") + (IsVulkan() ? "vk" : "dx12") + ".dll (error " + std::to_string(GetLastError()) + ")");
        return false;
    }
    g_ffxCreate    = reinterpret_cast<PfnFfxCreateContext>(GetProcAddress(g_loaderModule, "ffxCreateContext"));
    g_ffxDestroy   = reinterpret_cast<PfnFfxDestroyContext>(GetProcAddress(g_loaderModule, "ffxDestroyContext"));
    g_ffxConfigure = reinterpret_cast<PfnFfxConfigure>(GetProcAddress(g_loaderModule, "ffxConfigure"));
    g_ffxQuery     = reinterpret_cast<PfnFfxQuery>(GetProcAddress(g_loaderModule, "ffxQuery"));
    g_ffxDispatch  = reinterpret_cast<PfnFfxDispatch>(GetProcAddress(g_loaderModule, "ffxDispatch"));
    g_loadOk = g_fgModule && g_ffxCreate && g_ffxDestroy && g_ffxConfigure && g_ffxDispatch;
    if (!g_loadOk && g_fgModule)
        SetMessage("the AMD loader DLL is missing one of ffxCreateContext/DestroyContext/Configure/Dispatch");
    return g_loadOk;
}

// ---------------------------------------------------------------------------------------------------------------
// ffx FG context (shared by both backends). Caller holds g_fgMutex and has made sure the GPU is idle when a context
// is being replaced.
// ---------------------------------------------------------------------------------------------------------------
static void (*g_idleFn)() = nullptr;     // backend-specific "wait until nothing of ours is in flight"
static VkDeviceContext g_vkDevCtx{};

static void DestroyFgContext()
{
    if (g_fg)
    {
        if (g_idleFn) g_idleFn();
        g_ffxDestroy(&g_fg, nullptr);
        g_fg = nullptr;
    }
    g_fgW = g_fgH = 0;
    g_vram = 0;
}

static bool EnsureFgContext(const FGPrepareParams& p, uint32_t ffxFormat, uint32_t dispW, uint32_t dispH)
{
    const bool same = g_fg && g_fgCreateFlags == p.createFlags && g_fgW == dispW && g_fgH == dispH &&
                      g_fgMaxW == p.maxRenderW && g_fgMaxH == p.maxRenderH && g_fgFormat == ffxFormat;
    if (same)
        return true;
    if (g_fgFailed && g_fgCreateFlags == p.createFlags && g_fgW == dispW && g_fgH == dispH && g_fgFormat == ffxFormat)
        return false;

    DestroyFgContext();
    g_fgCreateFlags = p.createFlags; g_fgW = dispW; g_fgH = dispH; g_fgMaxW = p.maxRenderW; g_fgMaxH = p.maxRenderH; g_fgFormat = ffxFormat;
    g_fgFailed = false;
    g_resetNext = true;

    ffxCreateContextDescFrameGeneration create{};
    create.header.type  = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION;
    create.flags        = p.createFlags;
    create.displaySize  = { dispW, dispH };
    create.maxRenderSize= { p.maxRenderW, p.maxRenderH };
    create.backBufferFormat = ffxFormat;

    ffxCreateBackendDX12Desc backendDx{};
    backendDx.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_DX12;
    backendDx.device      = IsD3D12() ? g_d3d12->GetDevice() : nullptr;
    ffxCreateBackendVKDesc backendVk{};
    backendVk.header.type   = FFX_API_CREATE_CONTEXT_DESC_TYPE_BACKEND_VK;
    backendVk.deviceContext = g_vkDevCtx;
    ffxApiHeader& backendHeader = IsVulkan() ? backendVk.header : backendDx.header;

    ffxCreateContextDescFrameGenerationVersion ver{};
    ver.header.type = FFX_API_CREATE_CONTEXT_DESC_TYPE_FRAMEGENERATION_VERSION;
    ver.version     = FFX_FRAMEGENERATION_VERSION;

    create.header.pNext = &backendHeader;
    backendHeader.pNext = &ver.header;

    const ffxReturnCode_t rc = g_ffxCreate(&g_fg, &create.header, nullptr);
    if (rc != FFX_API_RETURN_OK || !g_fg)
    {
        g_fg = nullptr;
        g_fgFailed = true;
        g_lastReturn = (int)rc;
        g_state = kFgContextFailed;
        SetMessage("ffxCreateContext(FrameGeneration) failed, code " + std::to_string(rc) +
                   " (FG 4.x needs a Radeon RX 9000 series GPU; other GPUs use the 3.1 provider)");
        return false;
    }

    FfxApiEffectMemoryUsage mem{};
    ffxQueryDescFrameGenerationGetGPUMemoryUsage q{};
    q.header.type = FFX_API_QUERY_DESC_TYPE_FRAMEGENERATION_GPU_MEMORY_USAGE;
    q.gpuMemoryUsageFrameGeneration = &mem;
    if (g_ffxQuery && g_ffxQuery(&g_fg, &q.header) == FFX_API_RETURN_OK)
        g_vram = mem.totalUsageInBytes;
    return true;
}

// Configure every frame (cheap; mirrors the SDK sample's manual mode).
static void ConfigureFg(void* nativeSwapchain, uint64_t frameId, uint32_t w, uint32_t h, const FGConfig& cfg)
{
    ffxConfigureDescFrameGeneration c{};
    c.header.type = FFX_API_CONFIGURE_DESC_TYPE_FRAMEGENERATION;
    c.swapChain = nativeSwapchain;
    c.presentCallback = nullptr;
    c.frameGenerationCallback = nullptr;
    c.frameGenerationEnabled = true;
    c.allowAsyncWorkloads = false;
    c.HUDLessColor = {};
    c.flags = cfg.debugFlags | FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
    c.onlyPresentGenerated = false;
    c.generationRect = { 0, 0, (int32_t)w, (int32_t)h };
    c.frameID = frameId;
    const ffxReturnCode_t rc = g_ffxConfigure(&g_fg, &c.header);
    if (rc != FFX_API_RETURN_OK)
    {
        g_lastReturn = (int)rc;
        SetMessage("ffxConfigure(FrameGeneration) failed, code " + std::to_string(rc));
    }
}

static void FillPrepare(ffxDispatchDescFrameGenerationPrepareV2& d, const FGPrepareParams& p, uint64_t frameId, uint32_t flags)
{
    d.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE_V2;
    d.frameID = frameId;
    d.flags = flags | FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY;
    d.renderSize = { p.renderW, p.renderH };
    d.jitterOffset = { p.jitterX, p.jitterY };
    d.motionVectorScale = { p.mvScaleX, p.mvScaleY };
    d.frameTimeDelta = p.frameTimeMs;
    d.reset = p.reset != 0 || g_resetNext.exchange(false);
    d.cameraNear = p.cameraNear;
    d.cameraFar = p.cameraFar;
    d.cameraFovAngleVertical = p.cameraFovY;
    d.viewSpaceToMetersFactor = p.viewSpaceToMeters > 0.0f ? p.viewSpaceToMeters : 1.0f;
    std::memcpy(d.cameraPosition, p.camPos, sizeof(d.cameraPosition));
    std::memcpy(d.cameraUp, p.camUp, sizeof(d.cameraUp));
    std::memcpy(d.cameraRight, p.camRight, sizeof(d.cameraRight));
    std::memcpy(d.cameraForward, p.camForward, sizeof(d.cameraForward));
}

static void FillGenerate(ffxDispatchDescFrameGeneration& d, uint64_t frameId, uint32_t w, uint32_t h, const FGConfig& cfg, bool reset)
{
    d.header.type = FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION;
    d.numGeneratedFrames = 1;
    d.reset = reset;
    d.backbufferTransferFunction = (uint32_t)cfg.transferFunction;
    d.minMaxLuminance[0] = cfg.minLuminance;
    d.minMaxLuminance[1] = cfg.maxLuminance;
    d.generationRect = { 0, 0, (int32_t)w, (int32_t)h };
    d.frameID = frameId;
}

// Pacing + stats bookkeeping common to both hooks.
struct HookTiming
{
    double lastRealMs = 0.0;
    double baseMs = 0.0;
    double totalWaitedMs = 0.0; // diagnostics: all pacing waits so far
    double waitedMs = 0.0;      // time this plugin spent blocking the render thread since the last real frame
    double rawMs = 0.0;         // smoothed real-frame interval INCLUDING our waits (what the player sees as the frame rate)
    void NoteRealFrame()
    {
        const double now = NowMs();
        if (lastRealMs > 0.0)
        {
            const double raw = now - lastRealMs;
            if (raw > 0.0 && raw < 500.0)
                rawMs = rawMs <= 0.0 ? raw : rawMs * 0.9 + raw * 0.1;
            // The pacing wait must be computed from the interval the game would have WITHOUT that wait, otherwise the wait
            // (half an interval) lengthens the interval, which lengthens the wait ... and the real frame rate halves.
            double dt = raw - waitedMs;
            if (dt < 0.1) dt = 0.1;
            waitedMs = 0.0;
            if (dt > 0.0 && dt < 500.0)
                baseMs = baseMs <= 0.0 ? dt : baseMs * 0.9 + dt * 0.1;
        }
        lastRealMs = now;
        g_baseFrameMs = (float)baseMs;
    }
};
static HookTiming g_timing;

static double g_statFenceWaitMs = 0.0;   // diagnostics: time spent waiting for our own earlier command lists

// Wait half a real-frame interval after the generated frame so the real frame lands mid-way (only when pacing is on).
static void PaceHalfFrame(double tInterp)
{
    const double w0 = NowMs();
    PaceUntil(tInterp + 0.5 * g_timing.baseMs);
    const double waited = NowMs() - w0;
    g_timing.waitedMs += waited;
    g_timing.totalWaitedMs += waited;
}
static thread_local bool t_inHook = false;

// =================================================================================================================
// D3D12
// =================================================================================================================
static constexpr D3D12_RESOURCE_STATES kSrvAll =
    D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;

static DXGI_FORMAT TypedUnorm(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: case DXGI_FORMAT_R10G10B10A2_UNORM: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return DXGI_FORMAT_UNKNOWN;
    }
}
static uint32_t FfxFormatFromDxgi(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_UNORM:     return FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_UNORM:     return FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_UNORM:  return FFX_API_SURFACE_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return FFX_API_SURFACE_FORMAT_R16G16B16A16_FLOAT;
    default: return FFX_API_SURFACE_FORMAT_UNKNOWN;
    }
}

struct DxSlot
{
    ComPtr<ID3D12CommandAllocator>    allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    UINT64 fenceValue = 0;
};
static constexpr int kDxSlots = 16;   // 4 lists per generated frame: 16 lets the CPU run ~4 frames ahead instead of waiting on the GPU

struct DxState
{
    // swapchain / hook
    IDXGISwapChain* swapchain = nullptr;
    void**          vtbl = nullptr;
    HRESULT (STDMETHODCALLTYPE* origPresent)(IDXGISwapChain*, UINT, UINT) = nullptr;
    HRESULT (STDMETHODCALLTYPE* origPresent1)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*) = nullptr;
    UINT    (STDMETHODCALLTYPE* origGetIndex)(IDXGISwapChain3*) = nullptr;
    bool    hooked = false;
    // our own GPU objects
    ComPtr<ID3D12Resource> curCopy, interp;
    UINT64 width = 0; UINT height = 0; DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    ComPtr<ID3D12Fence> fence;
    UINT64 fenceValue = 0;
    HANDLE fenceEvent = nullptr;
    DxSlot hookSlots[kDxSlots];
    int hookSlotIndex = 0;
    DxSlot prepSlots[kDxSlots];
    int prepSlotIndex = 0;
};
static DxState g_dx;

static void DxWaitFence(UINT64 value)
{
    if (!g_dx.fence || value == 0 || g_dx.fence->GetCompletedValue() >= value)
        return;
    if (!g_dx.fenceEvent)
        g_dx.fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    const double w0 = NowMs();
    g_dx.fence->SetEventOnCompletion(value, g_dx.fenceEvent);
    WaitForSingleObject(g_dx.fenceEvent, 5000);
    g_statFenceWaitMs += NowMs() - w0;
}

// Blocks until everything this plugin submitted has finished and Unity's queue is idle.
static void DxIdle()
{
    if (!IsD3D12()) return;
    ID3D12Device* dev = g_d3d12->GetDevice();
    ID3D12CommandQueue* q = g_d3d12->GetCommandQueue();
    if (!dev || !q) return;
    if (!g_dx.fence)
        dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_dx.fence));
    if (!g_dx.fence) return;
    q->Signal(g_dx.fence.Get(), ++g_dx.fenceValue);
    DxWaitFence(g_dx.fenceValue);
}

// Diagnostics for the first few frames: wait for the GPU and log whether the device has been removed, so a GPU fault can be
// attributed to the stage that caused it. (Costs a GPU sync per stage, only while g_statPrepared is small.)
static void DxCheck(const char* tag)
{
    if (!IsD3D12() || g_statPrepared.load() > 6)
        return;
    ID3D12Device* dev = g_d3d12->GetDevice();
    DxIdle();
    FgLog("  [check] %s: GetDeviceRemovedReason = 0x%08x", tag, dev ? (unsigned)dev->GetDeviceRemovedReason() : 0xFFFFFFFFu);
}

static DxSlot* DxAcquire(ID3D12Device* dev, DxSlot* ring, int& index, bool waitOwnFence)
{
    DxSlot& s = ring[index];
    index = (index + 1) % kDxSlots;
    if (!s.allocator)
    {
        if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&s.allocator)))) return nullptr;
        if (FAILED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, s.allocator.Get(), nullptr, IID_PPV_ARGS(&s.list)))) { s.allocator.Reset(); return nullptr; }
        s.list->Close();
    }
    if (s.fenceValue != 0)
    {
        if (waitOwnFence)
            DxWaitFence(s.fenceValue);
        else if (ID3D12Fence* ff = g_d3d12->GetFrameFence(); ff && ff->GetCompletedValue() < s.fenceValue)
        {
            HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            ff->SetEventOnCompletion(s.fenceValue, ev);
            WaitForSingleObject(ev, 5000);
            CloseHandle(ev);
        }
    }
    if (FAILED(s.allocator->Reset()) || FAILED(s.list->Reset(s.allocator.Get(), nullptr)))
        return nullptr;
    return &s;
}

static D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* r, D3D12_RESOURCE_STATES a, D3D12_RESOURCE_STATES b)
{
    D3D12_RESOURCE_BARRIER bar{};
    bar.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    bar.Transition.pResource = r;
    bar.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    bar.Transition.StateBefore = a;
    bar.Transition.StateAfter = b;
    return bar;
}

static bool DxEnsureTextures(ID3D12Device* dev, const D3D12_RESOURCE_DESC& bb)
{
    const DXGI_FORMAT fmt = TypedUnorm(bb.Format);
    if (fmt == DXGI_FORMAT_UNKNOWN)
        return false;
    if (g_dx.curCopy && g_dx.interp && g_dx.width == bb.Width && g_dx.height == bb.Height && g_dx.fmt == fmt)
        return true;
    DxIdle();
    g_dx.curCopy.Reset(); g_dx.interp.Reset();
    D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_RESOURCE_DESC d{};
    d.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; d.Width = bb.Width; d.Height = bb.Height;
    d.DepthOrArraySize = 1; d.MipLevels = 1; d.Format = fmt; d.SampleDesc = { 1, 0 };
    d.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN; d.Flags = D3D12_RESOURCE_FLAG_NONE;
    if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, kSrvAll, nullptr, IID_PPV_ARGS(&g_dx.curCopy))))
        return false;
    d.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &d, kSrvAll, nullptr, IID_PPV_ARGS(&g_dx.interp))))
    { g_dx.curCopy.Reset(); return false; }
    g_dx.curCopy->SetName(L"FSR4FG current frame copy");
    g_dx.interp->SetName(L"FSR4FG interpolated frame");
    g_dx.width = bb.Width; g_dx.height = bb.Height; g_dx.fmt = fmt;
    return true;
}

static bool DxFillSwapchainInfo(uint32_t& w, uint32_t& h, DXGI_FORMAT& fmt, IDXGISwapChain* sc)
{
    DXGI_SWAP_CHAIN_DESC desc{};
    if (!sc || FAILED(sc->GetDesc(&desc)))
        return false;
    w = desc.BufferDesc.Width; h = desc.BufferDesc.Height; fmt = TypedUnorm(desc.BufferDesc.Format);
    return fmt != DXGI_FORMAT_UNKNOWN;
}

// ---- the present hook ----------------------------------------------------------------------------------------
static bool DxShouldGenerate(IDXGISwapChain* self, UINT flags)
{
    if (t_inHook || self != g_dx.swapchain || (flags & (DXGI_PRESENT_TEST | DXGI_PRESENT_DO_NOT_SEQUENCE | DXGI_PRESENT_RESTART)))
        return false;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (!g_cfg.enabled)
            return false;
    }
    if (g_state.load() != kFgActive || !g_fg)
        return false;
    return true;
}

// Diagnostic ladder for the D3D12 present path. Put a single digit in FSR4FrameGen.mode (next to the plugin DLL) to select:
//   0 = full frame generation (default)
//   1 = generate + write the interpolated frame into the backbuffer, ONE present (no extra present)
//   2 = real frame presented untouched, generated frame goes to the next backbuffer (two presents, FFX output never touches Unity's buffer)
//   3 = no FFX dispatch at all, just the double present (frame presented twice)
//   4 = like 0 but WITHOUT the index-preserving extra presents (the old behaviour that removed the device on frame 2)
// Windows Explorer often saves the file as "FSR4FrameGen.mode.txt", so that name is accepted too.
static int DxDiagMode()
{
    static int mode = -1;
    if (mode >= 0) return mode;
    mode = 0;
    HMODULE self = nullptr;
    wchar_t path[MAX_PATH] = {};
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&DxDiagMode, &self) &&
        GetModuleFileNameW(self, path, MAX_PATH))
    {
        wchar_t* dot = wcsrchr(path, L'.');
        const wchar_t* names[] = { L".mode", L".mode.txt" };
        bool found = false;
        for (const wchar_t* ext : names)
        {
            if (!dot) break;
            wcscpy_s(dot, path + MAX_PATH - dot, ext);
            if (FILE* f = _wfopen(path, L"r"))
            {
                int v = 0;
                if (fscanf(f, "%d", &v) == 1 && v >= 0 && v <= 4) mode = v;
                fclose(f);
                found = true;
                FgLog("mode file found: %ls", path);
                break;
            }
        }
        if (!found) FgLog("no mode file (looked for FSR4FrameGen.mode and FSR4FrameGen.mode.txt next to the DLL)");
    }
    FgLog("diagnostic mode = %d", mode);
    return mode;
}

template <typename PresentFn>
static HRESULT DxGeneratedPresent(IDXGISwapChain* self, PresentFn&& realPresent)
{
    const double t0 = NowMs();
    g_timing.NoteRealFrame();

    const uint64_t frame = g_preparedFrame.load();
    if (frame == 0 || frame == g_handledFrame.load())
    {
        g_statSkipped++;
        return realPresent();           // nothing was prepared for this present
    }
    const bool logThis = g_statGenerated.load() < 12;
    const int mode = DxDiagMode();
    if (logThis) FgLog("  generate frame %llu: begin (mode %d, device reason at entry 0x%08x)", (unsigned long long)frame, mode,
                       (unsigned)g_d3d12->GetDevice()->GetDeviceRemovedReason());

    ID3D12Device* dev = g_d3d12->GetDevice();
    ID3D12CommandQueue* queue = g_d3d12->GetCommandQueue();
    ComPtr<IDXGISwapChain3> sc3;
    ComPtr<ID3D12Resource> backA;
    if (!dev || !queue || FAILED(self->QueryInterface(IID_PPV_ARGS(&sc3))) ||
        FAILED(sc3->GetBuffer(sc3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&backA))))
    {
        g_handledFrame = frame; g_statSkipped++;
        return realPresent();
    }

    FGConfig cfg; { std::lock_guard<std::mutex> lock(g_stateMutex); cfg = g_cfg; }
    const D3D12_RESOURCE_DESC bbDesc = backA->GetDesc();
    if (!DxEnsureTextures(dev, bbDesc))
    {
        g_handledFrame = frame; g_statSkipped++;
        return realPresent();
    }
    if (!g_dx.fence)
        dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_dx.fence));

    if (logThis) FgLog("  textures ok %ux%u fmt=%d; backbuffer A=%p idx=%u", (unsigned)bbDesc.Width, bbDesc.Height, (int)bbDesc.Format, (void*)backA.Get(), sc3->GetCurrentBackBufferIndex());
    // ---- list 1: backbuffer -> curCopy, interpolate, interpolated -> backbuffer ---------------------------------
    DxSlot* slot = DxAcquire(dev, g_dx.hookSlots, g_dx.hookSlotIndex, true);
    bool generated = false;
    if (!slot)
        FgLog("  could not acquire a command list for list 1 (device removed? reason=0x%08x)", (unsigned)dev->GetDeviceRemovedReason());
    if (slot)
    {
        bool submit = true;
        ID3D12GraphicsCommandList* cl = slot->list.Get();
        ID3D12Resource* A = backA.Get();
        ID3D12Resource* cur = g_dx.curCopy.Get();
        ID3D12Resource* itp = g_dx.interp.Get();
        const uint32_t w = (uint32_t)bbDesc.Width, h = bbDesc.Height;

        D3D12_RESOURCE_BARRIER b1[2] = { Transition(A, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE),
                                         Transition(cur, kSrvAll, D3D12_RESOURCE_STATE_COPY_DEST) };
        cl->ResourceBarrier(2, b1);
        cl->CopyResource(cur, A);
        D3D12_RESOURCE_BARRIER b2 = Transition(cur, D3D12_RESOURCE_STATE_COPY_DEST, kSrvAll);
        cl->ResourceBarrier(1, &b2);

        ffxReturnCode_t rc;
        {
            std::lock_guard<std::mutex> lock(g_fgMutex);
            ffxDispatchDescFrameGeneration d{};
            FillGenerate(d, frame, w, h, cfg, false);
            d.commandList = cl;
            d.presentColor = ffxApiGetResourceDX12(cur, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
            d.outputs[0]   = ffxApiGetResourceDX12(itp, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
            rc = (mode == 3) ? FFX_API_RETURN_OK : g_ffxDispatch(&g_fg, &d.header);
        }
        if (rc != FFX_API_RETURN_OK)
        {
            g_lastReturn = (int)rc;
            SetMessage("ffxDispatch(FrameGeneration) failed, code " + std::to_string(rc));
            FgLog("  ffxDispatch(FrameGeneration) FAILED, code %d: dropping the recorded list and switching frame generation off", (int)rc);
            // The list is only half-recorded: never execute it (the backbuffer stays in PRESENT because the barrier never ran).
            submit = false;
            { std::lock_guard<std::mutex> lock(g_stateMutex); g_cfg.enabled = 0; }
            g_state = kFgStalled;
        }
        else
        {
            if (mode <= 1)
            {
                D3D12_RESOURCE_BARRIER b3[2] = { Transition(itp, kSrvAll, D3D12_RESOURCE_STATE_COPY_SOURCE),
                                                 Transition(A, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COPY_DEST) };
                cl->ResourceBarrier(2, b3);
                cl->CopyResource(A, itp);
                D3D12_RESOURCE_BARRIER b4[2] = { Transition(itp, D3D12_RESOURCE_STATE_COPY_SOURCE, kSrvAll),
                                                 Transition(A, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT) };
                cl->ResourceBarrier(2, b4);
            }
            else
            {
                D3D12_RESOURCE_BARRIER b3 = Transition(A, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_PRESENT);
                cl->ResourceBarrier(1, &b3);
            }
            generated = true;
        }
        cl->Close();
        if (submit)
        {
            ID3D12CommandList* lists[] = { cl };
            queue->ExecuteCommandLists(1, lists);
            queue->Signal(g_dx.fence.Get(), ++g_dx.fenceValue);
            slot->fenceValue = g_dx.fenceValue;
            DxCheck("after list 1 (copy + generate + copy back)");
        }
    }

    g_handledFrame = frame;
    if (logThis) FgLog("  list 1 submitted, generated=%d", generated ? 1 : 0);
    if (!generated)
    {
        g_statSkipped++;
        return realPresent();
    }

    if (mode == 1)
    {
        g_statGenerated++;
        const HRESULT hr1 = realPresent();
        if (logThis) FgLog("  mode 1: single present returned 0x%08x", (unsigned)hr1);
        return hr1;
    }

    // ---- present the interpolated frame (it sits in backbuffer A; in mode 2/3 it is the untouched real frame) -----
    if (logThis) FgLog("  presenting first frame");
    HRESULT hr = realPresent();
    const double tInterp = NowMs();
    if (logThis) FgLog("  first present returned 0x%08x", (unsigned)hr);
    if (FAILED(hr))
        return hr;

    // ---- list 2: the real frame goes into the next backbuffer -----------------------------------------------------
    ComPtr<ID3D12Resource> backB;
    DxSlot* slot2 = DxAcquire(dev, g_dx.hookSlots, g_dx.hookSlotIndex, true);
    if (slot2 && SUCCEEDED(sc3->GetBuffer(sc3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&backB))))
    {
        ID3D12GraphicsCommandList* cl = slot2->list.Get();
        ID3D12Resource* B = backB.Get();
        ID3D12Resource* cur = g_dx.curCopy.Get();
        ID3D12Resource* src = (mode == 2) ? g_dx.interp.Get() : cur;   // mode 2: the generated frame goes second
        D3D12_RESOURCE_BARRIER b1[2] = { Transition(B, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST),
                                         Transition(src, kSrvAll, D3D12_RESOURCE_STATE_COPY_SOURCE) };
        cl->ResourceBarrier(2, b1);
        cl->CopyResource(B, src);
        D3D12_RESOURCE_BARRIER b2[2] = { Transition(B, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT),
                                         Transition(src, D3D12_RESOURCE_STATE_COPY_SOURCE, kSrvAll) };
        cl->ResourceBarrier(2, b2);
        cl->Close();
        ID3D12CommandList* lists[] = { cl };
        queue->ExecuteCommandLists(1, lists);
        queue->Signal(g_dx.fence.Get(), ++g_dx.fenceValue);
        slot2->fenceValue = g_dx.fenceValue;
        DxCheck("after list 2 (real frame -> next backbuffer)");
    }

    g_statGenerated++;
    if (logThis) FgLog("  list 2 submitted (real frame -> next backbuffer), presenting real frame");
    if (cfg.pacing)
        PaceHalfFrame(tInterp);
    g_lastHookMs = (float)(NowMs() - t0);
    HRESULT hrLast = realPresent();   // the real frame
    if (logThis) FgLog("  second present returned 0x%08x (backbuffer index now %u)", (unsigned)hrLast, sc3->GetCurrentBackBufferIndex());

    // ---- keep DXGI's backbuffer index where Unity expects it ------------------------------------------------------
    // Unity tracks the backbuffer index itself and assumes ONE present per frame (index + 1). Two presents leave DXGI at
    // index + 2, Unity then renders into a buffer that is no longer current and the device is removed with
    // DXGI_ERROR_ACCESS_DENIED. With N buffers, 2 + (N - 1) presents bring the index back to index + 1 (mod N), so we
    // present the real frame N - 1 more times. The copies have identical content, so the screen keeps showing the real
    // frame; the cost is a few full-screen copies and presents. With VSync on every present waits for a vblank, so frame
    // generation needs VSync off.
    if (mode != 4 && SUCCEEDED(hrLast))
    {
        DXGI_SWAP_CHAIN_DESC scd{};
        sc3->GetDesc(&scd);
        const UINT n = scd.BufferCount ? scd.BufferCount : 1;
        ID3D12Resource* last = (mode == 2) ? g_dx.interp.Get() : g_dx.curCopy.Get();
        for (UINT k = 0; k + 1 < n && SUCCEEDED(hrLast); ++k)
        {
            ComPtr<ID3D12Resource> buf;
            DxSlot* s3 = DxAcquire(dev, g_dx.hookSlots, g_dx.hookSlotIndex, true);
            if (!s3 || FAILED(sc3->GetBuffer(sc3->GetCurrentBackBufferIndex(), IID_PPV_ARGS(&buf))))
            {
                FgLog("  could not fill an index-preserving present (device reason 0x%08x)", (unsigned)dev->GetDeviceRemovedReason());
                break;
            }
            ID3D12GraphicsCommandList* cl = s3->list.Get();
            D3D12_RESOURCE_BARRIER a[2] = { Transition(buf.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST),
                                            Transition(last, kSrvAll, D3D12_RESOURCE_STATE_COPY_SOURCE) };
            cl->ResourceBarrier(2, a);
            cl->CopyResource(buf.Get(), last);
            D3D12_RESOURCE_BARRIER b[2] = { Transition(buf.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT),
                                            Transition(last, D3D12_RESOURCE_STATE_COPY_SOURCE, kSrvAll) };
            cl->ResourceBarrier(2, b);
            cl->Close();
            ID3D12CommandList* lists[] = { cl };
            queue->ExecuteCommandLists(1, lists);
            queue->Signal(g_dx.fence.Get(), ++g_dx.fenceValue);
            s3->fenceValue = g_dx.fenceValue;
            hrLast = realPresent();
        }
        if (logThis) FgLog("  index-preserving presents done (%u buffers): index now %u, hr 0x%08x", n, sc3->GetCurrentBackBufferIndex(), (unsigned)hrLast);
    }

    // ---- performance log: every 240 generated frames, so a frame-rate drop can be attributed ------------------------
    {
        const double hookMs = NowMs() - t0;
        g_lastHookMs = (float)hookMs;
        static double accHook = 0.0, accFence0 = 0.0, accWait0 = 0.0;
        static int accN = 0;
        accHook += hookMs; ++accN;
        if (accN >= 240)
        {
            const double fenceMs = g_statFenceWaitMs - accFence0;
            const double waitMs = g_timing.totalWaitedMs - accWait0;
            FgLog("perf (%d frames): real %.1f fps (%.2f ms, %.2f ms without our waits) | hook avg %.2f ms, pacing wait avg %.2f ms, fence wait avg %.2f ms | pacing=%d",
                  accN, g_timing.rawMs > 0 ? 1000.0 / g_timing.rawMs : 0.0, g_timing.rawMs, g_timing.baseMs, accHook / accN, waitMs / accN, fenceMs / accN, cfg.pacing);
            accHook = 0.0; accN = 0; accFence0 = g_statFenceWaitMs; accWait0 = g_timing.totalWaitedMs;
        }
    }
    return hrLast;
}

#ifdef _MSC_VER
static int DxSehFilter(EXCEPTION_POINTERS* ep)
{
    FgLog("!! exception 0x%08lx at %p (%s) inside Present", ep->ExceptionRecord->ExceptionCode, ep->ExceptionRecord->ExceptionAddress,
          ModuleOf(ep->ExceptionRecord->ExceptionAddress).c_str());
    return EXCEPTION_EXECUTE_HANDLER;
}
static HRESULT DxSehPresent(HRESULT (STDMETHODCALLTYPE* fn)(IDXGISwapChain*, UINT, UINT), IDXGISwapChain* sc, UINT sync, UINT flags, bool* faulted)
{
    __try { return fn(sc, sync, flags); }
    __except (DxSehFilter(GetExceptionInformation())) { *faulted = true; return E_FAIL; }
}
static HRESULT DxSehPresent1(HRESULT (STDMETHODCALLTYPE* fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*), IDXGISwapChain1* sc,
                             UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params, bool* faulted)
{
    __try { return fn(sc, sync, flags, params); }
    __except (DxSehFilter(GetExceptionInformation())) { *faulted = true; return E_FAIL; }
}
#else
static HRESULT DxSehPresent(HRESULT (STDMETHODCALLTYPE* fn)(IDXGISwapChain*, UINT, UINT), IDXGISwapChain* sc, UINT sync, UINT flags, bool*)
{ return fn(sc, sync, flags); }
static HRESULT DxSehPresent1(HRESULT (STDMETHODCALLTYPE* fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*), IDXGISwapChain1* sc,
                             UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params, bool*)
{ return fn(sc, sync, flags, params); }
#endif

// A Present inside a generated frame faulted: stop generating for the rest of the session instead of taking the game down.
static void DxDisableAfterFault()
{
    { std::lock_guard<std::mutex> lock(g_stateMutex); g_cfg.enabled = 0; }
    g_state = kFgStalled;
    SetMessage("an exception occurred while presenting a generated frame; frame generation was switched off (see FSR4FrameGen.log)");
}

static std::atomic<int> g_presentCalls{ 0 };

static HRESULT STDMETHODCALLTYPE DxHookPresent(IDXGISwapChain* self, UINT sync, UINT flags)
{
    const int call = ++g_presentCalls;
    const bool generate = DxShouldGenerate(self, flags);
    if (call <= 30 || (generate && call <= 120))
        FgLog("Present #%d self=%p sync=%u flags=0x%x tid=%lu generate=%d prepared=%llu handled=%llu", call, (void*)self, sync, flags,
              GetCurrentThreadId(), generate ? 1 : 0, (unsigned long long)g_preparedFrame.load(), (unsigned long long)g_handledFrame.load());
    if (!generate)
        return g_dx.origPresent(self, sync, flags);
    t_inHook = true;
    bool faulted = false;
    HRESULT hr = DxGeneratedPresent(self, [&] { return DxSehPresent(g_dx.origPresent, self, sync, flags, &faulted); });
    t_inHook = false;
    if (faulted)
    {
        DxDisableAfterFault();
        return S_OK;
    }
    return hr;
}

static HRESULT STDMETHODCALLTYPE DxHookPresent1(IDXGISwapChain1* self, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
{
    const int call = ++g_presentCalls;
    const bool generate = DxShouldGenerate(self, flags);
    if (call <= 30 || (generate && call <= 120))
        FgLog("Present1 #%d self=%p sync=%u flags=0x%x tid=%lu generate=%d prepared=%llu handled=%llu", call, (void*)self, sync, flags,
              GetCurrentThreadId(), generate ? 1 : 0, (unsigned long long)g_preparedFrame.load(), (unsigned long long)g_handledFrame.load());
    if (!generate)
        return g_dx.origPresent1(self, sync, flags, params);
    t_inHook = true;
    bool faulted = false;
    // Present1 dereferences its parameter block, so nullptr is NOT allowed (it crashed inside dxgi.dll). An all-zero block means
    // "no dirty rects, no scroll", which is what a generated frame needs.
    const DXGI_PRESENT_PARAMETERS emptyParams{};
    HRESULT hr = DxGeneratedPresent(self, [&] { return DxSehPresent1(g_dx.origPresent1, self, sync, flags, &emptyParams, &faulted); });
    t_inHook = false;
    if (faulted)
    {
        DxDisableAfterFault();
        return S_OK;
    }
    return hr;
}

static std::atomic<int> g_getIndexCalls{ 0 };
static UINT STDMETHODCALLTYPE DxHookGetIndex(IDXGISwapChain3* self)
{
    const UINT v = g_dx.origGetIndex(self);
    const int n = ++g_getIndexCalls;
    if (n <= 40)
    {
#ifdef _MSC_VER
        void* ret = _ReturnAddress();
#else
        void* ret = __builtin_return_address(0);
#endif
        FgLog("GetCurrentBackBufferIndex #%d -> %u (caller %s)", n, v, ModuleOf(ret).c_str());
    }
    return v;
}

static bool DxInstallHook(IDXGISwapChain* sc)
{
    if (g_dx.hooked && g_dx.swapchain == sc)
        return true;
    if (!sc) return false;
    void** vtbl = *reinterpret_cast<void***>(sc);
    DWORD oldProt = 0;
    // slots: 8 Present, 22 Present1, 36 GetCurrentBackBufferIndex (IDXGISwapChain3)
    ComPtr<IDXGISwapChain3> sc3;
    const bool has3 = SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&sc3)));
    const SIZE_T span = sizeof(void*) * (has3 ? 29 : 15);
    if (!VirtualProtect(&vtbl[8], span, PAGE_READWRITE, &oldProt))
        return false;
    if (vtbl[8] != reinterpret_cast<void*>(&DxHookPresent))
    {
        g_dx.origPresent  = reinterpret_cast<decltype(g_dx.origPresent)>(vtbl[8]);
        g_dx.origPresent1 = reinterpret_cast<decltype(g_dx.origPresent1)>(vtbl[22]);
        vtbl[8]  = reinterpret_cast<void*>(&DxHookPresent);
        vtbl[22] = reinterpret_cast<void*>(&DxHookPresent1);
        if (has3 && vtbl[36] != reinterpret_cast<void*>(&DxHookGetIndex))
        {
            g_dx.origGetIndex = reinterpret_cast<decltype(g_dx.origGetIndex)>(vtbl[36]);
            vtbl[36] = reinterpret_cast<void*>(&DxHookGetIndex);
        }
    }
    DWORD tmp = 0;
    VirtualProtect(&vtbl[8], span, oldProt, &tmp);
    FlushInstructionCache(GetCurrentProcess(), &vtbl[8], span);
    g_dx.swapchain = sc; g_dx.vtbl = vtbl; g_dx.hooked = true;
    FgLog("hook installed: swapchain=%p vtbl=%p origPresent=%p (%s) origPresent1=%p (%s)", (void*)sc, (void*)vtbl, (void*)g_dx.origPresent,
          ModuleOf((void*)g_dx.origPresent).c_str(), (void*)g_dx.origPresent1, ModuleOf((void*)g_dx.origPresent1).c_str());
    return true;
}

static void DxRemoveHook()
{
    if (!g_dx.hooked || !g_dx.vtbl) return;
    DWORD oldProt = 0;
    const SIZE_T span = sizeof(void*) * (g_dx.origGetIndex ? 29 : 15);
    if (VirtualProtect(&g_dx.vtbl[8], span, PAGE_READWRITE, &oldProt))
    {
        if (g_dx.vtbl[8] == reinterpret_cast<void*>(&DxHookPresent))  g_dx.vtbl[8]  = reinterpret_cast<void*>(g_dx.origPresent);
        if (g_dx.vtbl[22] == reinterpret_cast<void*>(&DxHookPresent1)) g_dx.vtbl[22] = reinterpret_cast<void*>(g_dx.origPresent1);
        if (g_dx.origGetIndex && g_dx.vtbl[36] == reinterpret_cast<void*>(&DxHookGetIndex)) g_dx.vtbl[36] = reinterpret_cast<void*>(g_dx.origGetIndex);
        DWORD tmp = 0;
        VirtualProtect(&g_dx.vtbl[8], span, oldProt, &tmp);
    }
    g_dx.hooked = false; g_dx.swapchain = nullptr; g_dx.vtbl = nullptr;
}

// ---- the prepare event (render thread) -----------------------------------------------------------------------
static void WaitForHook(uint64_t frame)
{
    // The previous frame's present must have run before this frame's prepare is recorded, otherwise the FG context
    // would see prepare(N+1) before dispatch(N). Bounded wait; repeated timeouts switch generation off.
    const double start = NowMs();
    while (g_handledFrame.load() < frame && NowMs() - start < 60.0)
        Sleep(0);
    if (g_handledFrame.load() < frame)
    {
        if (++g_stallStrikes >= 5)
        {
            g_state = kFgStalled;
            SetMessage("the present hook did not run for 5 consecutive frames; frame generation was switched off");
        }
        g_handledFrame = frame;   // do not wait for it again
    }
    else
        g_stallStrikes = 0;
}

static void DxPrepare(const FGPrepareParams& p)
{
    IDXGISwapChain* sc = g_d3d12->GetSwapChain();
    if (!sc)
    {
        g_state = kFgNoSwapchain;
        SetMessage("no swapchain: D3D12 frame generation only works in Player builds, not in the Editor");
        return;
    }
    uint32_t w = 0, h = 0; DXGI_FORMAT fmt = DXGI_FORMAT_UNKNOWN;
    if (!DxFillSwapchainInfo(w, h, fmt, sc))
    {
        g_state = kFgUnsupportedFormat;
        SetMessage("unsupported backbuffer format (supported: R8G8B8A8, B8G8R8A8, R10G10B10A2, R16G16B16A16_FLOAT)");
        return;
    }
    if (!DxInstallHook(sc))
    {
        g_state = kFgNoSwapchain;
        SetMessage("could not hook the swapchain Present");
        return;
    }
    if (g_state.load() == kFgStalled)
        return;

    ID3D12Device* dev = g_d3d12->GetDevice();
    FGConfig cfg; { std::lock_guard<std::mutex> lock(g_stateMutex); cfg = g_cfg; }
    if (g_preparedFrame.load() > g_handledFrame.load())
        WaitForHook(g_preparedFrame.load());

    {
        std::lock_guard<std::mutex> lock(g_fgMutex);
        g_idleFn = &DxIdle;
        if (!EnsureFgContext(p, FfxFormatFromDxgi(fmt), w, h))
            return;
        if (g_state.load() != kFgActive)
            g_state = kFgActive;

        const uint64_t frame = ++g_frameId;
        ConfigureFg(sc, frame, w, h, cfg);

        DxSlot* slot = DxAcquire(dev, g_dx.prepSlots, g_dx.prepSlotIndex, false);
        if (!slot) { SetMessage("could not acquire a command list"); return; }

        ffxDispatchDescFrameGenerationPrepareV2 d{};
        FillPrepare(d, p, frame, cfg.debugFlags);
        d.commandList  = slot->list.Get();
        d.depth        = ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.depth),         FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
        d.motionVectors= ffxApiGetResourceDX12(static_cast<ID3D12Resource*>(p.motionVectors), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
        const ffxReturnCode_t rc = g_ffxDispatch(&g_fg, &d.header);
        slot->list->Close();
        if (g_statPrepared.load() < 6)
            FgLog("prepare frame %llu: ffxDispatch(PrepareV2) rc=%d render=%ux%u depth=%p mv=%p", (unsigned long long)frame, (int)rc, p.renderW, p.renderH, p.depth, p.motionVectors);
        if (rc != FFX_API_RETURN_OK)
        {
            g_lastReturn = (int)rc;
            SetMessage("ffxDispatch(FrameGenerationPrepareV2) failed, code " + std::to_string(rc));
            return;
        }
        UnityGraphicsD3D12ResourceState states[2] = {
            { static_cast<ID3D12Resource*>(p.depth),         kSrvAll, kSrvAll },
            { static_cast<ID3D12Resource*>(p.motionVectors), kSrvAll, kSrvAll } };
        slot->fenceValue = g_d3d12->ExecuteCommandList(slot->list.Get(), 2, states);
        DxCheck("after prepare");
        g_preparedFrame = frame;
        g_statPrepared++;
    }
}

// =================================================================================================================
// Vulkan
// =================================================================================================================
static PFN_vkVoidFunction g_vkOrigPresent = nullptr, g_vkOrigCreateSwapchain = nullptr, g_vkOrigDestroySwapchain = nullptr;

struct VkFns
{
    PFN_vkGetDeviceProcAddr gdpa = nullptr;
    PFN_vkCreateImage createImage; PFN_vkDestroyImage destroyImage; PFN_vkGetImageMemoryRequirements getMemReq;
    PFN_vkAllocateMemory allocMem; PFN_vkFreeMemory freeMem; PFN_vkBindImageMemory bindMem;
    PFN_vkCreateCommandPool createPool; PFN_vkDestroyCommandPool destroyPool; PFN_vkAllocateCommandBuffers allocCb;
    PFN_vkResetCommandBuffer resetCb; PFN_vkBeginCommandBuffer beginCb; PFN_vkEndCommandBuffer endCb;
    PFN_vkCmdPipelineBarrier cmdBarrier; PFN_vkCmdCopyImage cmdCopy;
    PFN_vkQueueSubmit queueSubmit; PFN_vkCreateSemaphore createSem; PFN_vkDestroySemaphore destroySem;
    PFN_vkCreateFence createFence; PFN_vkDestroyFence destroyFence; PFN_vkWaitForFences waitFences; PFN_vkResetFences resetFences;
    PFN_vkGetSwapchainImagesKHR getScImages; PFN_vkAcquireNextImageKHR acquire; PFN_vkQueuePresentKHR present;
    PFN_vkCreateSwapchainKHR createSc; PFN_vkDeviceWaitIdle deviceWaitIdle;
    PFN_vkGetPhysicalDeviceMemoryProperties getMemProps;
    bool loaded = false;
};
static VkFns g_vf;

struct VkSlot { VkCommandBuffer cb = VK_NULL_HANDLE; VkFence fence = VK_NULL_HANDLE; VkSemaphore semAcquire = VK_NULL_HANDLE, semGen = VK_NULL_HANDLE, semReal = VK_NULL_HANDLE; bool submitted = false; };
static constexpr int kVkSlots = 3;

struct VkState
{
    VkQueue queue = VK_NULL_HANDLE;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    VkFormat swapFormat = VK_FORMAT_UNDEFINED; VkExtent2D extent{};
    std::vector<VkImage> images;
    bool createdThroughHook = false;
    bool extraImage = false;
    VkImage curCopy = VK_NULL_HANDLE, interp = VK_NULL_HANDLE;
    VkDeviceMemory curMem = VK_NULL_HANDLE, interpMem = VK_NULL_HANDLE;
    VkImageCreateInfo curCi{}, interpCi{};
    VkImageLayout curLayout = VK_IMAGE_LAYOUT_UNDEFINED, interpLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkSlot slots[kVkSlots]; int slotIndex = 0;
    VkFormat ourFormat = VK_FORMAT_UNDEFINED;
};
static VkState g_vs;

static VkFormat VkUnormFor(VkFormat f)
{
    switch (f)
    {
    case VK_FORMAT_R8G8B8A8_UNORM: case VK_FORMAT_R8G8B8A8_SRGB: return VK_FORMAT_R8G8B8A8_UNORM;
    case VK_FORMAT_B8G8R8A8_UNORM: case VK_FORMAT_B8G8R8A8_SRGB: return VK_FORMAT_B8G8R8A8_UNORM;
    case VK_FORMAT_A2B10G10R10_UNORM_PACK32: return VK_FORMAT_A2B10G10R10_UNORM_PACK32;
    case VK_FORMAT_R16G16B16A16_SFLOAT: return VK_FORMAT_R16G16B16A16_SFLOAT;
    default: return VK_FORMAT_UNDEFINED;
    }
}
static uint32_t FfxFormatFromVk(VkFormat f)
{
    switch (f)
    {
    case VK_FORMAT_R8G8B8A8_UNORM: return FFX_API_SURFACE_FORMAT_R8G8B8A8_UNORM;
    case VK_FORMAT_B8G8R8A8_UNORM: return FFX_API_SURFACE_FORMAT_B8G8R8A8_UNORM;
    case VK_FORMAT_A2B10G10R10_UNORM_PACK32: return FFX_API_SURFACE_FORMAT_R10G10B10A2_UNORM;
    case VK_FORMAT_R16G16B16A16_SFLOAT: return FFX_API_SURFACE_FORMAT_R16G16B16A16_FLOAT;
    default: return FFX_API_SURFACE_FORMAT_UNKNOWN;
    }
}

static bool VkLoadFns()
{
    if (g_vf.loaded) return true;
    const UnityVulkanInstance inst = g_vk->Instance();
    g_vf.gdpa = reinterpret_cast<PFN_vkGetDeviceProcAddr>(inst.getInstanceProcAddr(inst.instance, "vkGetDeviceProcAddr"));
    if (!g_vf.gdpa) return false;
    VkDevice d = inst.device;
#define LOADFN(member, name) g_vf.member = reinterpret_cast<decltype(g_vf.member)>(g_vf.gdpa(d, name)); if (!g_vf.member) return false;
    LOADFN(createImage, "vkCreateImage") LOADFN(destroyImage, "vkDestroyImage") LOADFN(getMemReq, "vkGetImageMemoryRequirements")
    LOADFN(allocMem, "vkAllocateMemory") LOADFN(freeMem, "vkFreeMemory") LOADFN(bindMem, "vkBindImageMemory")
    LOADFN(createPool, "vkCreateCommandPool") LOADFN(destroyPool, "vkDestroyCommandPool") LOADFN(allocCb, "vkAllocateCommandBuffers")
    LOADFN(resetCb, "vkResetCommandBuffer") LOADFN(beginCb, "vkBeginCommandBuffer") LOADFN(endCb, "vkEndCommandBuffer")
    LOADFN(cmdBarrier, "vkCmdPipelineBarrier") LOADFN(cmdCopy, "vkCmdCopyImage") LOADFN(queueSubmit, "vkQueueSubmit")
    LOADFN(createSem, "vkCreateSemaphore") LOADFN(destroySem, "vkDestroySemaphore") LOADFN(createFence, "vkCreateFence")
    LOADFN(destroyFence, "vkDestroyFence") LOADFN(waitFences, "vkWaitForFences") LOADFN(resetFences, "vkResetFences")
    LOADFN(getScImages, "vkGetSwapchainImagesKHR") LOADFN(acquire, "vkAcquireNextImageKHR") LOADFN(deviceWaitIdle, "vkDeviceWaitIdle")
#undef LOADFN
    g_vf.present = reinterpret_cast<PFN_vkQueuePresentKHR>(g_vkOrigPresent);
    g_vf.createSc = reinterpret_cast<PFN_vkCreateSwapchainKHR>(g_vkOrigCreateSwapchain);
    g_vf.getMemProps = reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(inst.getInstanceProcAddr(inst.instance, "vkGetPhysicalDeviceMemoryProperties"));
    g_vf.loaded = g_vf.present && g_vf.getMemProps;
    return g_vf.loaded;
}

static void VkIdle()
{
    if (g_vf.loaded && g_vkDevCtx.vkDevice)
        g_vf.deviceWaitIdle(g_vkDevCtx.vkDevice);
}

static void VkImgBarrier(VkCommandBuffer cb, VkImage img, VkImageLayout from, VkImageLayout to)
{
    VkImageMemoryBarrier b{};
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.srcAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
    b.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
    b.oldLayout = from; b.newLayout = to;
    b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    g_vf.cmdBarrier(cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
}

static void VkCopyFull(VkCommandBuffer cb, VkImage src, VkImage dst, VkExtent2D e)
{
    VkImageCopy r{};
    r.srcSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
    r.dstSubresource = r.srcSubresource;
    r.extent = { e.width, e.height, 1 };
    g_vf.cmdCopy(cb, src, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dst, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &r);
}

static bool VkCreateOwnImage(VkImage& image, VkDeviceMemory& mem, VkImageCreateInfo& ci, VkFormat fmt, VkExtent2D e, VkImageUsageFlags usage)
{
    ci = {};
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D; ci.format = fmt; ci.extent = { e.width, e.height, 1 };
    ci.mipLevels = 1; ci.arrayLayers = 1; ci.samples = VK_SAMPLE_COUNT_1_BIT; ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = usage; ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE; ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (g_vf.createImage(g_vkDevCtx.vkDevice, &ci, nullptr, &image) != VK_SUCCESS) return false;
    VkMemoryRequirements req{}; g_vf.getMemReq(g_vkDevCtx.vkDevice, image, &req);
    VkPhysicalDeviceMemoryProperties props{}; g_vf.getMemProps(g_vkDevCtx.vkPhysicalDevice, &props);
    uint32_t type = UINT32_MAX;
    for (uint32_t i = 0; i < props.memoryTypeCount; ++i)
        if ((req.memoryTypeBits & (1u << i)) && (props.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) { type = i; break; }
    if (type == UINT32_MAX) return false;
    VkMemoryAllocateInfo ai{}; ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO; ai.allocationSize = req.size; ai.memoryTypeIndex = type;
    if (g_vf.allocMem(g_vkDevCtx.vkDevice, &ai, nullptr, &mem) != VK_SUCCESS) return false;
    return g_vf.bindMem(g_vkDevCtx.vkDevice, image, mem, 0) == VK_SUCCESS;
}

static void VkDestroyOwnResources()
{
    if (!g_vf.loaded) return;
    VkIdle();
    VkDevice d = g_vkDevCtx.vkDevice;
    for (VkSlot& s : g_vs.slots)
    {
        if (s.fence) g_vf.destroyFence(d, s.fence, nullptr);
        if (s.semAcquire) g_vf.destroySem(d, s.semAcquire, nullptr);
        if (s.semGen) g_vf.destroySem(d, s.semGen, nullptr);
        if (s.semReal) g_vf.destroySem(d, s.semReal, nullptr);
        s = VkSlot{};
    }
    if (g_vs.pool) { g_vf.destroyPool(d, g_vs.pool, nullptr); g_vs.pool = VK_NULL_HANDLE; }
    if (g_vs.curCopy) { g_vf.destroyImage(d, g_vs.curCopy, nullptr); g_vs.curCopy = VK_NULL_HANDLE; }
    if (g_vs.interp)  { g_vf.destroyImage(d, g_vs.interp, nullptr);  g_vs.interp = VK_NULL_HANDLE; }
    if (g_vs.curMem)  { g_vf.freeMem(d, g_vs.curMem, nullptr);       g_vs.curMem = VK_NULL_HANDLE; }
    if (g_vs.interpMem) { g_vf.freeMem(d, g_vs.interpMem, nullptr);  g_vs.interpMem = VK_NULL_HANDLE; }
    g_vs.curLayout = g_vs.interpLayout = VK_IMAGE_LAYOUT_UNDEFINED;
}

static bool VkEnsureResources()
{
    const VkFormat fmt = VkUnormFor(g_vs.swapFormat);
    if (fmt == VK_FORMAT_UNDEFINED) return false;
    if (g_vs.curCopy && g_vs.interp && g_vs.ourFormat == fmt && g_vs.curCi.extent.width == g_vs.extent.width && g_vs.curCi.extent.height == g_vs.extent.height && g_vs.pool)
        return true;
    VkDestroyOwnResources();
    VkDevice d = g_vkDevCtx.vkDevice;
    if (!VkCreateOwnImage(g_vs.curCopy, g_vs.curMem, g_vs.curCi, fmt, g_vs.extent,
                          VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_SAMPLED_BIT)) return false;
    if (!VkCreateOwnImage(g_vs.interp, g_vs.interpMem, g_vs.interpCi, fmt, g_vs.extent,
                          VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT)) return false;
    VkCommandPoolCreateInfo pi{}; pi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT; pi.queueFamilyIndex = g_vk->Instance().queueFamilyIndex;
    if (g_vf.createPool(d, &pi, nullptr, &g_vs.pool) != VK_SUCCESS) return false;
    for (VkSlot& s : g_vs.slots)
    {
        VkCommandBufferAllocateInfo ai{}; ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        ai.commandPool = g_vs.pool; ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; ai.commandBufferCount = 1;
        if (g_vf.allocCb(d, &ai, &s.cb) != VK_SUCCESS) return false;
        VkFenceCreateInfo fi{}; fi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
        VkSemaphoreCreateInfo si{}; si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        if (g_vf.createFence(d, &fi, nullptr, &s.fence) != VK_SUCCESS || g_vf.createSem(d, &si, nullptr, &s.semAcquire) != VK_SUCCESS ||
            g_vf.createSem(d, &si, nullptr, &s.semGen) != VK_SUCCESS || g_vf.createSem(d, &si, nullptr, &s.semReal) != VK_SUCCESS) return false;
    }
    g_vs.ourFormat = fmt;
    return true;
}

static FfxApiResource VkOwnResource(VkImage img, const VkImageCreateInfo& ci, uint32_t state)
{
    return ffxApiGetResourceVK(reinterpret_cast<void*>(img), ffxApiGetImageResourceDescriptionVK(img, ci, 0), state);
}

static void VkFetchSwapchainImages()
{
    if (!g_vs.swapchain || !g_vk || !VkLoadFns())
        return;
    VkDevice device = g_vk->Instance().device;
    uint32_t n = 0;
    g_vf.getScImages(device, g_vs.swapchain, &n, nullptr);
    g_vs.images.resize(n);
    g_vf.getScImages(device, g_vs.swapchain, &n, g_vs.images.data());
}

// ---- hooks ---------------------------------------------------------------------------------------------------
static VKAPI_ATTR VkResult VKAPI_CALL VkHookCreateSwapchain(VkDevice device, const VkSwapchainCreateInfoKHR* pCreateInfo,
                                                            const VkAllocationCallbacks* pAllocator, VkSwapchainKHR* pSwapchain)
{
    auto orig = reinterpret_cast<PFN_vkCreateSwapchainKHR>(g_vkOrigCreateSwapchain);
    VkSwapchainCreateInfoKHR ci = *pCreateInfo;
    ci.imageUsage |= VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;   // we copy to/from swapchain images
    ci.minImageCount += 1;                                                                  // one spare image for the generated frame
    VkResult r = orig(device, &ci, pAllocator, pSwapchain);
    bool extra = true;
    if (r != VK_SUCCESS)
    {
        ci.minImageCount = pCreateInfo->minImageCount;
        extra = false;
        r = orig(device, &ci, pAllocator, pSwapchain);
    }
    if (r == VK_SUCCESS)
    {
        g_vs.swapchain = *pSwapchain;
        g_vs.swapFormat = pCreateInfo->imageFormat;
        g_vs.extent = pCreateInfo->imageExtent;
        g_vs.createdThroughHook = true;
        g_vs.extraImage = extra;
        g_vs.images.clear();
        g_vs.queue = VK_NULL_HANDLE;
        VkFetchSwapchainImages();   // retried from VkPrepare if the device functions are not available yet
    }
    return r;
}

static VKAPI_ATTR void VKAPI_CALL VkHookDestroySwapchain(VkDevice device, VkSwapchainKHR sc, const VkAllocationCallbacks* pAllocator)
{
    if (sc == g_vs.swapchain)
    {
        std::lock_guard<std::mutex> lock(g_fgMutex);
        VkDestroyOwnResources();
        g_vs.swapchain = VK_NULL_HANDLE; g_vs.images.clear();
    }
    reinterpret_cast<PFN_vkDestroySwapchainKHR>(g_vkOrigDestroySwapchain)(device, sc, pAllocator);
}

static VKAPI_ATTR VkResult VKAPI_CALL VkHookQueuePresent(VkQueue queue, const VkPresentInfoKHR* pi)
{
    auto orig = reinterpret_cast<PFN_vkQueuePresentKHR>(g_vkOrigPresent);
    bool enabled; { std::lock_guard<std::mutex> lock(g_stateMutex); enabled = g_cfg.enabled != 0; }
    if (!enabled || t_inHook || !g_fg || g_state.load() != kFgActive || pi->swapchainCount != 1 || pi->pSwapchains[0] != g_vs.swapchain ||
        !g_vs.createdThroughHook || g_vs.images.empty() || queue != g_vk->Instance().graphicsQueue || !g_vf.loaded)
        return orig(queue, pi);

    t_inHook = true;
    struct Reset { ~Reset() { t_inHook = false; } } resetGuard;

    const double t0 = NowMs();
    g_timing.NoteRealFrame();
    const uint64_t frame = g_preparedFrame.load();
    if (frame == 0 || frame == g_handledFrame.load()) { g_statSkipped++; return orig(queue, pi); }

    FGConfig cfg; { std::lock_guard<std::mutex> lock(g_stateMutex); cfg = g_cfg; }
    VkDevice dev = g_vkDevCtx.vkDevice;
    const uint32_t imgIdx = pi->pImageIndices[0];
    if (imgIdx >= g_vs.images.size()) { g_handledFrame = frame; return orig(queue, pi); }
    VkImage A = g_vs.images[imgIdx];

    if (!VkEnsureResources()) { g_handledFrame = frame; g_statSkipped++; g_state = kFgUnsupportedFormat; SetMessage("Vulkan frame generation: unsupported swapchain format or resource creation failed"); return orig(queue, pi); }

    VkSlot& s = g_vs.slots[g_vs.slotIndex]; g_vs.slotIndex = (g_vs.slotIndex + 1) % kVkSlots;
    if (s.submitted) { g_vf.waitFences(dev, 1, &s.fence, VK_TRUE, 2000000000ull); g_vf.resetFences(dev, 1, &s.fence); s.submitted = false; }

    uint32_t imgB = 0;
    if (g_vf.acquire(dev, g_vs.swapchain, 2000000000ull, s.semAcquire, VK_NULL_HANDLE, &imgB) < 0 || imgB >= g_vs.images.size() || imgB == imgIdx)
    {
        // Could not get a spare image (swapchain without an extra image, or out of date): pass through.
        g_handledFrame = frame; g_statSkipped++;
        return orig(queue, pi);
    }
    VkImage B = g_vs.images[imgB];

    g_vf.resetCb(s.cb, 0);
    VkCommandBufferBeginInfo bi{}; bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO; bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    g_vf.beginCb(s.cb, &bi);

    // A: PRESENT_SRC -> TRANSFER_SRC; curCopy: -> TRANSFER_DST -> copy -> SHADER_READ_ONLY
    VkImgBarrier(s.cb, A, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    VkImgBarrier(s.cb, g_vs.curCopy, g_vs.curLayout, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    VkCopyFull(s.cb, A, g_vs.curCopy, g_vs.extent);
    VkImgBarrier(s.cb, g_vs.curCopy, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
    g_vs.curLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    if (g_vs.interpLayout != VK_IMAGE_LAYOUT_GENERAL)
    {
        VkImgBarrier(s.cb, g_vs.interp, g_vs.interpLayout, VK_IMAGE_LAYOUT_GENERAL);
        g_vs.interpLayout = VK_IMAGE_LAYOUT_GENERAL;
    }

    ffxReturnCode_t rc;
    {
        std::lock_guard<std::mutex> lock(g_fgMutex);
        ffxDispatchDescFrameGeneration d{};
        FillGenerate(d, frame, g_vs.extent.width, g_vs.extent.height, cfg, false);
        d.commandList  = s.cb;
        d.presentColor = VkOwnResource(g_vs.curCopy, g_vs.curCi, FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
        d.outputs[0]   = VkOwnResource(g_vs.interp, g_vs.interpCi, FFX_API_RESOURCE_STATE_UNORDERED_ACCESS);
        rc = g_ffxDispatch(&g_fg, &d.header);
    }
    const bool ok = rc == FFX_API_RETURN_OK;
    if (!ok) { g_lastReturn = (int)rc; SetMessage("ffxDispatch(FrameGeneration, Vulkan) failed, code " + std::to_string(rc)); }

    // generated frame -> image B (UNDEFINED -> TRANSFER_DST -> PRESENT_SRC); A back to PRESENT_SRC
    VkImgBarrier(s.cb, B, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    if (ok)
    {
        VkImgBarrier(s.cb, g_vs.interp, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
        VkCopyFull(s.cb, g_vs.interp, B, g_vs.extent);
        VkImgBarrier(s.cb, g_vs.interp, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL);
    }
    else
    {
        // interpolation failed: show the real frame twice so the swapchain image we acquired is still presented
        VkImgBarrier(s.cb, g_vs.curCopy, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
        VkCopyFull(s.cb, g_vs.curCopy, B, g_vs.extent);
        VkImgBarrier(s.cb, g_vs.curCopy, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL);
    }
    VkImgBarrier(s.cb, B, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
    VkImgBarrier(s.cb, A, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
    g_vf.endCb(s.cb);

    // Wait on Unity's render-finished semaphores and our acquire; signal one semaphore per present.
    std::vector<VkSemaphore> waits(pi->pWaitSemaphores, pi->pWaitSemaphores + pi->waitSemaphoreCount);
    waits.push_back(s.semAcquire);
    std::vector<VkPipelineStageFlags> stages(waits.size(), VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    VkSemaphore signals[2] = { s.semGen, s.semReal };
    VkSubmitInfo si{}; si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.waitSemaphoreCount = (uint32_t)waits.size(); si.pWaitSemaphores = waits.data(); si.pWaitDstStageMask = stages.data();
    si.commandBufferCount = 1; si.pCommandBuffers = &s.cb; si.signalSemaphoreCount = 2; si.pSignalSemaphores = signals;
    if (g_vf.queueSubmit(queue, 1, &si, s.fence) != VK_SUCCESS)
    {
        g_handledFrame = frame; g_statSkipped++;
        return orig(queue, pi);         // our submit failed: the original waits are still unconsumed
    }
    s.submitted = true;
    g_handledFrame = frame;

    // present #1: the generated frame (image B)
    VkPresentInfoKHR p1{}; p1.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
    p1.waitSemaphoreCount = 1; p1.pWaitSemaphores = &s.semGen; p1.swapchainCount = 1; p1.pSwapchains = &g_vs.swapchain; p1.pImageIndices = &imgB;
    VkResult r1 = g_vf.present(queue, &p1);
    const double tInterp = NowMs();
    if (ok && r1 >= 0) g_statGenerated++; else g_statSkipped++;

    if (cfg.pacing && ok)
        PaceHalfFrame(tInterp);

    // present #2: the real frame (image A), waiting on our semaphore instead of Unity's
    VkPresentInfoKHR p2 = *pi;
    p2.waitSemaphoreCount = 1; p2.pWaitSemaphores = &s.semReal;
    g_lastHookMs = (float)(NowMs() - t0);
    return g_vf.present(queue, &p2);
}

static void VkPrepare(const FGPrepareParams& p)
{
    if (g_vs.createdThroughHook && g_vs.images.empty())
        VkFetchSwapchainImages();
    if (!g_vs.createdThroughHook || g_vs.images.empty())
    {
        g_state = kFgNoSwapchain;
        SetMessage("Vulkan: the swapchain was created before the plugin's hook ran. The plugin must be preloaded in the Player (the package's .meta does this) and frame generation only works in Player builds");
        return;
    }
    if (g_state.load() == kFgStalled)
        return;
    if (!VkLoadFns())
    {
        g_state = kFgUnsupportedApi;
        SetMessage("Vulkan: could not resolve the device functions needed for frame generation");
        return;
    }
    if (g_preparedFrame.load() > g_handledFrame.load())
        WaitForHook(g_preparedFrame.load());

    FGConfig cfg; { std::lock_guard<std::mutex> lock(g_stateMutex); cfg = g_cfg; }
    UnityVulkanRecordingState rs{};
    if (!g_vk->CommandRecordingState(&rs, kUnityVulkanGraphicsQueueAccess_DontCare))
        return;

    const uint32_t fmtFfx = FfxFormatFromVk(VkUnormFor(g_vs.swapFormat));
    if (fmtFfx == FFX_API_SURFACE_FORMAT_UNKNOWN) { g_state = kFgUnsupportedFormat; SetMessage("Vulkan: unsupported swapchain format"); return; }

    UnityVulkanImage depth{}, mv{};
    const VkAccessFlags rd = VK_ACCESS_SHADER_READ_BIT;
    if (!g_vk->AccessTexture(p.depth, UnityVulkanWholeImage, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, rd, kUnityVulkanResourceAccess_PipelineBarrier, &depth) ||
        !g_vk->AccessTexture(p.motionVectors, UnityVulkanWholeImage, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, rd, kUnityVulkanResourceAccess_PipelineBarrier, &mv))
    { SetMessage("Vulkan: Unity could not provide the depth / motion vector textures"); return; }
    if (!g_vk->CommandRecordingState(&rs, kUnityVulkanGraphicsQueueAccess_DontCare) || !rs.commandBuffer)
        return;

    std::lock_guard<std::mutex> lock(g_fgMutex);
    g_idleFn = &VkIdle;
    if (!EnsureFgContext(p, fmtFfx, g_vs.extent.width, g_vs.extent.height))
        return;
    if (g_state.load() != kFgActive) g_state = kFgActive;

    const uint64_t frame = ++g_frameId;
    ConfigureFg(reinterpret_cast<void*>(g_vs.swapchain), frame, g_vs.extent.width, g_vs.extent.height, cfg);

    auto mk = [](const UnityVulkanImage& img) {
        VkImageCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO; ci.imageType = img.type; ci.format = img.format; ci.extent = img.extent;
        ci.mipLevels = (uint32_t)(img.mipCount > 0 ? img.mipCount : 1); ci.arrayLayers = (uint32_t)(img.layers > 0 ? img.layers : 1);
        ci.samples = img.samples; ci.tiling = img.tiling; ci.usage = img.usage;
        return ffxApiGetResourceVK(reinterpret_cast<void*>(img.image), ffxApiGetImageResourceDescriptionVK(img.image, ci, 0), FFX_API_RESOURCE_STATE_PIXEL_COMPUTE_READ);
    };
    ffxDispatchDescFrameGenerationPrepareV2 d{};
    FillPrepare(d, p, frame, cfg.debugFlags);
    d.commandList = rs.commandBuffer;
    d.depth = mk(depth);
    d.motionVectors = mk(mv);
    const ffxReturnCode_t rc = g_ffxDispatch(&g_fg, &d.header);
    if (rc != FFX_API_RETURN_OK)
    {
        g_lastReturn = (int)rc;
        SetMessage("ffxDispatch(FrameGenerationPrepareV2, Vulkan) failed, code " + std::to_string(rc));
        return;
    }
    g_preparedFrame = frame;
    g_statPrepared++;
}

// =================================================================================================================
// Events / Unity plugin glue
// =================================================================================================================
static void ShutdownDevice()
{
    std::lock_guard<std::mutex> lock(g_fgMutex);
    if (IsD3D12())
    {
        DxIdle();
        DxRemoveHook();
        g_dx.curCopy.Reset(); g_dx.interp.Reset();
        for (auto& s : g_dx.hookSlots) s = DxSlot{};
        for (auto& s : g_dx.prepSlots) s = DxSlot{};
    }
    if (g_fg && g_ffxDestroy)
    {
        g_ffxDestroy(&g_fg, nullptr);
        g_fg = nullptr;
    }
    if (IsVulkan())
        VkDestroyOwnResources();
    g_state = kFgIdle;
}

static void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
{
    if (eventId == kEventDestroy)
    {
        std::lock_guard<std::mutex> lock(g_fgMutex);
        DestroyFgContext();
        g_preparedFrame = 0; g_handledFrame = 0; g_frameId = 0;
        if (g_state.load() == kFgActive) g_state = kFgIdle;
        return;
    }
    if (eventId != kEventPrepare || !data)
        return;
    const FGPrepareParams& p = *static_cast<const FGPrepareParams*>(data);
    if (!(IsD3D12() || IsVulkan())) { g_state = kFgUnsupportedApi; return; }
    if (!p.depth || !p.motionVectors) return;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (!g_cfg.enabled) return;
    }
    if (!LoadAmdLoader()) { g_state = kFgDllMissing; return; }
    if (IsD3D12()) DxPrepare(p); else VkPrepare(p);
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
    switch (eventType)
    {
    case kUnityGfxDeviceEventInitialize:
        g_renderer = g_gfx->GetRenderer();
        if (g_renderer == kUnityGfxRendererD3D12)
        {
            g_d3d12 = g_unity->Get<IUnityGraphicsD3D12v8>();
            if (g_d3d12)
            {
                // Prepare records into our own list and hands it to Unity with ExecuteCommandList; flush earlier work first.
                UnityD3D12PluginEventConfig cfg{};
                cfg.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
                cfg.flags = kUnityD3D12EventConfigFlag_FlushCommandBuffers | kUnityD3D12EventConfigFlag_SyncWorkerThreads;
                g_d3d12->ConfigureEvent(kEventPrepare, &cfg);
                g_d3d12->ConfigureEvent(kEventDestroy, &cfg);
            }
        }
        else if (g_renderer == kUnityGfxRendererVulkan)
        {
            g_vk = g_unity->Get<IUnityGraphicsVulkan>();
            if (g_vk)
            {
                const UnityVulkanInstance inst = g_vk->Instance();
                g_vkDevCtx.vkDevice = inst.device;
                g_vkDevCtx.vkPhysicalDevice = inst.physicalDevice;
                g_vkDevCtx.vkDeviceProcAddr = reinterpret_cast<PFN_vkGetDeviceProcAddr>(inst.getInstanceProcAddr(inst.instance, "vkGetDeviceProcAddr"));
                UnityVulkanPluginEventConfig prep{};
                prep.renderPassPrecondition = kUnityVulkanRenderPass_EnsureOutside;
                prep.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_DontCare;
                prep.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission | kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState;
                g_vk->ConfigureEvent(kEventPrepare, &prep);
                UnityVulkanPluginEventConfig destroy{};
                destroy.renderPassPrecondition = kUnityVulkanRenderPass_DontCare;
                destroy.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_Allow;
                destroy.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission | kUnityVulkanEventConfigFlag_FlushCommandBuffers | kUnityVulkanEventConfigFlag_SyncWorkerThreads;
                g_vk->ConfigureEvent(kEventDestroy, &destroy);
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
    g_gfx = g_unity->Get<IUnityGraphics>();
    g_gfx->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

    // Vulkan: the swapchain must be created through our hook (extra usage flags + one spare image). That only works
    // when this plugin is loaded before Unity creates the swapchain, i.e. it must be a preloaded plugin.
    if (g_gfx->GetRenderer() == kUnityGfxRendererVulkan)
    {
        IUnityGraphicsVulkan* vk = g_unity->Get<IUnityGraphicsVulkan>();
        if (vk)
        {
            g_vk = vk;
            g_vkOrigCreateSwapchain  = vk->InterceptVulkanAPI("vkCreateSwapchainKHR",  reinterpret_cast<PFN_vkVoidFunction>(&VkHookCreateSwapchain));
            g_vkOrigDestroySwapchain = vk->InterceptVulkanAPI("vkDestroySwapchainKHR", reinterpret_cast<PFN_vkVoidFunction>(&VkHookDestroySwapchain));
            g_vkOrigPresent          = vk->InterceptVulkanAPI("vkQueuePresentKHR",     reinterpret_cast<PFN_vkVoidFunction>(&VkHookQueuePresent));
        }
    }
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
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
FG_EXPORT UnityRenderingEventAndData FG_GetRenderEventFunc() { return OnRenderEvent; }

FG_EXPORT void FG_SetConfig(const FGConfig* cfg)
{
    if (!cfg) return;
    std::lock_guard<std::mutex> lock(g_stateMutex);
    const bool wasEnabled = g_cfg.enabled != 0;
    g_cfg = *cfg;
    if (!wasEnabled && cfg->enabled)
    {
        g_resetNext = true;
        if (g_state.load() == kFgStalled) { g_state = kFgIdle; g_stallStrikes = 0; }
    }
}

FG_EXPORT void FG_GetStats(FGStats* out)
{
    if (!out) return;
    out->state = g_state.load();
    out->backend = IsD3D12() ? 1 : IsVulkan() ? 2 : 0;
    out->lastReturn = g_lastReturn.load();
    out->pad = 0;
    out->prepared = g_statPrepared.load();
    out->generated = g_statGenerated.load();
    out->skipped = g_statSkipped.load();
    out->vramBytes = g_vram.load() + (g_dx.width && g_dx.height ? 2ull * g_dx.width * g_dx.height * 4ull : 0ull);
    out->baseFrameMs = g_baseFrameMs.load();
    out->lastHookMs = g_lastHookMs.load();
}

// Writes the latest diagnostic message (UTF-8, NUL terminated) into buf.
FG_EXPORT int FG_GetLastMessage(char* buf, int capacity)
{
    std::lock_guard<std::mutex> lock(g_stateMutex);
    if (!buf || capacity <= 0) return (int)g_lastMessage.size();
    const int n = std::min<int>((int)g_lastMessage.size(), capacity - 1);
    std::memcpy(buf, g_lastMessage.data(), n);
    buf[n] = 0;
    return n;
}

// 1 = D3D12, 2 = Vulkan, 0 = unsupported renderer.
FG_EXPORT int FG_GetBackend() { return IsD3D12() ? 1 : IsVulkan() ? 2 : 0; }

// Lets C# verify that its struct mirrors match this build.
FG_EXPORT void FG_GetStructSizes(int* prepare, int* config, int* stats)
{
    if (prepare) *prepare = (int)sizeof(FGPrepareParams);
    if (config)  *config  = (int)sizeof(FGConfig);
    if (stats)   *stats   = (int)sizeof(FGStats);
}
