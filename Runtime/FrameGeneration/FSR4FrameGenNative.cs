#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>Must match FGPrepareParams in Native~/src/fsr4_framegen.cpp byte for byte (136 bytes, 8-byte packing).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FGPrepareParams
    {
        public uint createFlags;
        public uint maxRenderW, maxRenderH;
        public uint renderW, renderH;
        public uint displayW, displayH;
        public int reset;
        public float jitterX, jitterY;
        public float mvScaleX, mvScaleY;
        public float frameTimeMs;
        public float cameraNear, cameraFar, cameraFovY;
        public float viewSpaceToMeters;
        public Vector3 camPos, camUp, camRight, camForward;
        /// <summary>Time.frameCount of the frame being prepared (0 = unknown). The native plugin accepts one prepare per frame.</summary>
        public int frameIndex;
        public IntPtr depth, motionVectors;
    }

    /// <summary>Must match FGHudlessParams (24 bytes). Payload of the optional HUD-less event (D3D12 only).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FGHudlessParams
    {
        public IntPtr texture;
        public uint width, height;
        public uint reserved0, reserved1;
    }

    /// <summary>Must match FGConfig (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FGConfig
    {
        public int enabled;
        public int pacing;
        public uint debugFlags;
        public int transferFunction;
        public float minLuminance, maxLuminance;
        public int reserved0, reserved1;
    }

    /// <summary>Must match FGStats (56 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct FGStats
    {
        public int state;
        public int backend;
        public int lastReturn;
        public int pad;
        public ulong prepared, generated, skipped, vramBytes;
        public float baseFrameMs, lastHookMs;
    }

    /// <summary>P/Invoke surface of FSR4FrameGen.dll plus the plugin-event plumbing.</summary>
    internal static unsafe class FGNative
    {
        const string Dll = "FSR4FrameGen";

        public const int EventPrepare = 1;
        public const int EventDestroy = 2;
        public const int EventHudless = 3;

        // Parameter blocks are read by the render thread some time after the event is issued, so they live in a ring of
        // unmanaged slots (same approach as the core upscaler).
        const int RingSize = 16;
        static IntPtr s_Ring;
        static int s_RingIndex;
        static IntPtr s_HudlessRing;
        static int s_HudlessRingIndex;
        static int s_HudlessSupported = -1;   // -1 unknown, 0 no, 1 yes
        static IntPtr s_RenderEventFunc;

        [DllImport(Dll)] static extern IntPtr FG_GetRenderEventFunc();
        [DllImport(Dll)] static extern void FG_SetConfig(FGConfig* cfg);
        [DllImport(Dll)] static extern void FG_GetStats(FGStats* stats);
        [DllImport(Dll)] static extern int FG_GetLastMessage(byte* buffer, int capacity);
        [DllImport(Dll)] static extern int FG_GetBackend();
        [DllImport(Dll)] static extern void FG_GetStructSizes(int* prepare, int* config, int* stats);
        [DllImport(Dll)] static extern int FG_GetProviderVersion(byte* buffer, int capacity);
        [DllImport(Dll)] static extern ulong FG_GetHudlessFrames();

        public static bool TryInitialize(out string error)
        {
            error = null;
            var api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D12 && api != GraphicsDeviceType.Vulkan)
            {
                error = $"Frame generation needs the Direct3D12 or Vulkan graphics API (current: {api}).";
                return false;
            }
            try
            {
                int a, b, c;
                FG_GetStructSizes(&a, &b, &c);
                if (a != sizeof(FGPrepareParams) || b != sizeof(FGConfig) || c != sizeof(FGStats))
                {
                    error = "FSR4FrameGen.dll does not match this package version (struct sizes differ). Rebuild it with Native~/build.bat.";
                    return false;
                }
                s_RenderEventFunc = FG_GetRenderEventFunc();
                return s_RenderEventFunc != IntPtr.Zero;
            }
            catch (DllNotFoundException)
            {
                error = "FSR4FrameGen.dll was not found in the package's Plugins/x86_64 folder. Build it with Native~/build.bat.";
                return false;
            }
            catch (EntryPointNotFoundException e)
            {
                error = "FSR4FrameGen.dll is missing an export - rebuild it. " + e.Message;
                return false;
            }
        }

        public static void SetConfig(in FGConfig cfg)
        {
            FGConfig copy = cfg;
            FG_SetConfig(&copy);
        }

        public static FGStats GetStats()
        {
            FGStats s;
            FG_GetStats(&s);
            return s;
        }

        public static string LastMessage()
        {
            byte* buf = stackalloc byte[512];
            int len = FG_GetLastMessage(buf, 512);
            return len <= 0 ? string.Empty : Encoding.UTF8.GetString(buf, Math.Min(len, 511));
        }

        /// <summary>Provider version of the frame-generation context. Empty until one exists, or when the DLL predates the export.</summary>
        public static string ProviderVersion()
        {
            try
            {
                byte* buf = stackalloc byte[96];
                int len = FG_GetProviderVersion(buf, 96);
                return len <= 0 ? string.Empty : Encoding.UTF8.GetString(buf, Math.Min(len, 95));
            }
            catch (EntryPointNotFoundException) { return string.Empty; }   // older FSR4FrameGen.dll: rebuild it to get this
            catch (DllNotFoundException) { return string.Empty; }
        }

        /// <summary>
        /// True when the loaded FSR4FrameGen.dll understands the HUD-less event (it was built from this version of the source).
        /// An older DLL does not export the counter and ignores the event, so the feature stays off instead of failing.
        /// </summary>
        public static bool HudlessSupported
        {
            get
            {
                if (s_HudlessSupported < 0)
                {
                    try { FG_GetHudlessFrames(); s_HudlessSupported = 1; }
                    catch (EntryPointNotFoundException) { s_HudlessSupported = 0; }
                    catch (DllNotFoundException) { s_HudlessSupported = 0; }
                }
                return s_HudlessSupported == 1;
            }
        }

        /// <summary>Frames that were handed a HUD-less image by the native plugin so far.</summary>
        public static ulong HudlessFrames()
        {
            try { return HudlessSupported ? FG_GetHudlessFrames() : 0UL; }
            catch (Exception) { return 0UL; }
        }

        /// <summary>Tells the native plugin which texture holds the HUD-less image of the frame that was just prepared.</summary>
        public static void IssueHudless(CommandBuffer cmd, IntPtr texture, uint width, uint height)
        {
            if (s_RenderEventFunc == IntPtr.Zero || texture == IntPtr.Zero)
                return;
            if (s_HudlessRing == IntPtr.Zero)
                s_HudlessRing = Marshal.AllocHGlobal(sizeof(FGHudlessParams) * RingSize);
            IntPtr slot = s_HudlessRing + (s_HudlessRingIndex % RingSize) * sizeof(FGHudlessParams);
            s_HudlessRingIndex++;
            *(FGHudlessParams*)slot = new FGHudlessParams { texture = texture, width = width, height = height };
            cmd.IssuePluginEventAndData(s_RenderEventFunc, EventHudless, slot);
        }

        public static string BackendName()
        {
            try { return FG_GetBackend() switch { 1 => "Direct3D12", 2 => "Vulkan", _ => "none" }; }
            catch (Exception) { return "unknown"; }
        }

        public static void Prepare(CommandBuffer cmd, in FGPrepareParams p)
        {
            if (s_Ring == IntPtr.Zero)
                s_Ring = Marshal.AllocHGlobal(sizeof(FGPrepareParams) * RingSize);
            IntPtr slot = s_Ring + (s_RingIndex % RingSize) * sizeof(FGPrepareParams);
            s_RingIndex++;
            *(FGPrepareParams*)slot = p;
            cmd.IssuePluginEventAndData(s_RenderEventFunc, EventPrepare, slot);
        }

        /// <summary>Releases the FFX context (and its GPU memory) on the render thread.</summary>
        public static void QueueDestroy()
        {
            if (s_RenderEventFunc == IntPtr.Zero)
                return;
            var cmd = new CommandBuffer { name = "FSR4 FrameGen destroy" };
            cmd.IssuePluginEventAndData(s_RenderEventFunc, EventDestroy, IntPtr.Zero);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }
    }
}
#endif
