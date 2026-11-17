#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>Mirrors FGState in the native plugin.</summary>
    public enum FSR4FrameGenState
    {
        Idle = 0,
        Active = 1,
        UnsupportedApi = 2,
        DllMissing = 3,
        NoSwapchain = 4,
        ContextFailed = 5,
        UnsupportedFormat = 6,
        Stalled = 7,
        /// <summary>The native plugin could not be loaded at all (see <see cref="FSR4FrameGeneration.statusMessage"/>).</summary>
        PluginUnavailable = 100,
    }

    /// <summary>Mirrors FfxApiDispatchFramegenerationFlags (debug overlays drawn into the generated frame).</summary>
    [Flags]
    public enum FSR4FrameGenDebug
    {
        None = 0,
        TearLines = 1 << 0,
        ResetIndicators = 1 << 1,
        DebugView = 1 << 2,
        PacingLines = 1 << 4,
    }

    public readonly struct FSR4FrameGenStats
    {
        public readonly FSR4FrameGenState state;
        public readonly ulong preparedFrames, generatedFrames, passedThroughFrames, vramBytes;
        /// <summary>Smoothed time between real (rendered) frames, in milliseconds.</summary>
        public readonly float baseFrameMs;
        public readonly float lastPresentHookMs;
        public readonly int lastFfxReturnCode;

        internal FSR4FrameGenStats(in FGStats s)
        {
            state = (FSR4FrameGenState)s.state;
            preparedFrames = s.prepared; generatedFrames = s.generated; passedThroughFrames = s.skipped; vramBytes = s.vramBytes;
            baseFrameMs = s.baseFrameMs; lastPresentHookMs = s.lastHookMs; lastFfxReturnCode = s.lastReturn;
        }
    }

    /// <summary>
    /// Experimental AMD FidelityFX frame generation (frame interpolation) for the FSR4 package. Off by default.
    /// Works in Player builds on Direct3D12 and Vulkan; the Editor has no swapchain to present to, so it only reports a status there.
    /// Enable from code (<c>FSR4FrameGeneration.enabled = true</c>), with the <see cref="FSR4FrameGenerationController"/> component, from the debug
    /// HUD, or by starting the player with <c>-fsr4-framegen</c>.
    /// </summary>
    public static class FSR4FrameGeneration
    {
        static bool s_Enabled;
        static bool s_Pacing = true;
        static FSR4FrameGenDebug s_Debug;
        static bool s_Initialised;
        static bool s_Available;
        static string s_Message = "";
        static FGConfig s_LastPushed;
        static bool s_HavePushed;
        static Camera s_SourceCamera;
        static int s_LastPrepareFrame = -1;
        static bool s_WarnedMultiCamera;

        /// <summary>Requested state. Frame generation only runs when this is true and <see cref="state"/> becomes Active.</summary>
        public static bool enabled
        {
            get => s_Enabled;
            set
            {
                if (s_Enabled == value) return;
                s_Enabled = value;
                if (value) Initialise();
                if (s_Available)
                {
                    PushConfig(true);
                    if (!value) FGNative.QueueDestroy();   // give the GPU memory back
                }
            }
        }

        /// <summary>Wait half a frame interval between the generated and the real frame when VSync does not do it already.</summary>
        public static bool pacing
        {
            get => s_Pacing;
            set { s_Pacing = value; if (s_Available) PushConfig(false); }
        }

        public static FSR4FrameGenDebug debug
        {
            get => s_Debug;
            set { s_Debug = value; if (s_Available) PushConfig(false); }
        }

        /// <summary>
        /// The camera whose depth and motion vectors drive frame generation. Null (the default) means the first camera that is
        /// upscaled in each frame. Frame generation interpolates the whole backbuffer from one camera's data, so set this when
        /// more than one camera is upscaled, for example a camera that renders into a texture next to the main camera.
        /// </summary>
        public static Camera sourceCamera
        {
            get => s_SourceCamera;
            set => s_SourceCamera = value;
        }

        /// <summary>False when the native plugin is missing / unsupported (e.g. wrong graphics API). See <see cref="statusMessage"/>.</summary>
        public static bool isAvailable { get { Initialise(); return s_Available; } }

        /// <summary>Last diagnostic message from the native plugin (or why it is unavailable).</summary>
        public static string statusMessage
        {
            get
            {
                Initialise();
                if (!s_Available) return s_Message;
                string m = FGNative.LastMessage();
                return string.IsNullOrEmpty(m) ? s_Message : m;
            }
        }

        public static FSR4FrameGenState state
        {
            get
            {
                Initialise();
                return s_Available ? (FSR4FrameGenState)FGNative.GetStats().state : FSR4FrameGenState.PluginUnavailable;
            }
        }

        public static FSR4FrameGenStats GetStats()
        {
            Initialise();
            if (!s_Available) return default;
            FGStats s = FGNative.GetStats();
            return new FSR4FrameGenStats(in s);
        }

        public static string backendName => s_Available ? FGNative.BackendName() : "none";

        /// <summary>
        /// Version name of the frame-generation provider that is actually running, as reported by FidelityFX. Empty until
        /// generation has started, and always empty with an FSR4FrameGen.dll built before this query existed (rebuild it).
        /// </summary>
        public static string providerInUse => s_Available ? FGNative.ProviderVersion() : string.Empty;

        /// <summary>True once the native plugin has been probed. Lets UI avoid loading it just to draw a status line.</summary>
        internal static bool initialised => s_Initialised;

        // ------------------------------------------------------------------------------------------------------
        static void Initialise()
        {
            if (s_Initialised) return;
            s_Initialised = true;
            s_Available = FGNative.TryInitialize(out s_Message);
            if (!s_Available)
                Debug.LogWarning("[FSR4 FrameGen] Unavailable: " + s_Message);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RuntimeInit()
        {
            // Domain reload can be disabled in the Editor: reset all static state so a new play session starts clean.
            s_Enabled = false; s_Initialised = false; s_Available = false; s_HavePushed = false; s_Message = "";
            s_SourceCamera = null; s_LastPrepareFrame = -1; s_WarnedMultiCamera = false;

            FSR4Hooks.UpscaleDispatched -= OnUpscaleDispatched;
            FSR4Hooks.UpscaleDispatched += OnUpscaleDispatched;
            FSR4HudSections.Register(FSR4FrameGenHudSection.instance);   // shown by the optional debug HUD module, if present

            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, "-fsr4-framegen", StringComparison.OrdinalIgnoreCase))
                    enabled = true;
        }

        static void PushConfig(bool force)
        {
            var cfg = new FGConfig
            {
                enabled = s_Enabled ? 1 : 0,
                pacing = s_Pacing ? 1 : 0,
                debugFlags = (uint)s_Debug,
                transferFunction = 0,               // FFX_API_BACKBUFFER_TRANSFER_FUNCTION_SRGB
                minLuminance = 0f,
                maxLuminance = 0f,
            };
            var hdr = HDROutputSettings.main;
            if (hdr != null && hdr.available && hdr.active)
            {
                // scRGB for an FP16 swapchain, PQ otherwise.
                cfg.transferFunction = hdr.format == RenderTextureFormat.ARGBHalf ? 2 : 1;
                cfg.minLuminance = hdr.minToneMapLuminance;
                cfg.maxLuminance = hdr.maxToneMapLuminance;
            }
            if (!force && s_HavePushed && s_LastPushed.enabled == cfg.enabled && s_LastPushed.pacing == cfg.pacing &&
                s_LastPushed.debugFlags == cfg.debugFlags && s_LastPushed.transferFunction == cfg.transferFunction &&
                s_LastPushed.minLuminance == cfg.minLuminance && s_LastPushed.maxLuminance == cfg.maxLuminance)
                return;
            FGNative.SetConfig(in cfg);
            s_LastPushed = cfg;
            s_HavePushed = true;
        }

        // ------------------------------------------------------------------------------------------------------
        // Called from inside the upscale pass: record FFX frame-generation "prepare" (depth, motion vectors, camera).
        // ------------------------------------------------------------------------------------------------------
        static void OnUpscaleDispatched(CommandBuffer cmd, in FSR4FrameData f)
        {
            if (!s_Enabled)
                return;
            Initialise();
            if (!s_Available)
                return;

            // The swapchain is presented once per frame, so exactly one camera can feed frame generation per frame. A second
            // prepare in the same frame used to wait for a present that had not happened yet and stalled the module.
            if (s_SourceCamera != null && f.camera != s_SourceCamera)
                return;
            int frameIndex = Time.frameCount;
            if (frameIndex == s_LastPrepareFrame)
            {
                if (!s_WarnedMultiCamera)
                {
                    s_WarnedMultiCamera = true;
                    Debug.LogWarning("[FSR4 FrameGen] More than one camera was upscaled in the same frame. Frame generation uses the first one; " +
                                     "set FSR4FrameGeneration.sourceCamera to choose which camera drives it.");
                }
                return;
            }
            s_LastPrepareFrame = frameIndex;

            PushConfig(false);   // picks up HDR output changes

            uint flags = 0;
            if (f.displayResolutionMotionVectors) flags |= 1u << 1;   // FFX_FRAMEGENERATION_ENABLE_DISPLAY_RESOLUTION_MOTION_VECTORS
            if (f.motionVectorJitterCancelled)    flags |= 1u << 2;   // ..._MOTION_VECTORS_JITTER_CANCELLATION
            if (f.depthInverted)                  flags |= 1u << 3;   // ..._DEPTH_INVERTED
            var hdr = HDROutputSettings.main;
            if (hdr != null && hdr.available && hdr.active) flags |= 1u << 5;   // ..._HIGH_DYNAMIC_RANGE

            var p = new FGPrepareParams
            {
                createFlags = flags,
                maxRenderW = (uint)f.maxRenderResolution.x, maxRenderH = (uint)f.maxRenderResolution.y,
                renderW = (uint)f.renderResolution.x, renderH = (uint)f.renderResolution.y,
                displayW = (uint)f.displayResolution.x, displayH = (uint)f.displayResolution.y,
                reset = f.resetHistory ? 1 : 0,
                jitterX = f.jitter.x, jitterY = f.jitter.y,
                mvScaleX = f.motionVectorScale.x, mvScaleY = f.motionVectorScale.y,
                frameTimeMs = f.frameTimeMs,
                cameraNear = f.cameraNear, cameraFar = f.cameraFar, cameraFovY = f.cameraFovY,
                viewSpaceToMeters = 1f,
                camPos = f.cameraPosition, camUp = f.cameraUp, camRight = f.cameraRight, camForward = f.cameraForward,
                frameIndex = frameIndex,
                depth = f.depth, motionVectors = f.motionVectors,
            };
            FGNative.Prepare(cmd, in p);
        }
    }
}
#endif
