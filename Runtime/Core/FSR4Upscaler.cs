#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AchiraStudio.FSR4
{
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    static class RegisterFSR4
    {
        static RegisterFSR4() => UpscalerRegistry.Register<FSR4Upscaler, FSR4Options>(FSR4Upscaler.upscalerName);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitRuntime() => UpscalerRegistry.Register<FSR4Upscaler, FSR4Options>(FSR4Upscaler.upscalerName);
    }

    /// <summary>Managed handle for one native ffx context (the native side creates it lazily on the render thread).</summary>
    public sealed class FSR4NativeContext
    {
        public readonly int id = FSR4Native.AllocContextId();
    }

    public class FSR4UpscalerContext : PluginUpscalerContext<FSR4NativeContext, FSR4Options>
    {
        public FSR4Quality createdWithQuality { get; }
        public ulong createdWithVersionOverride { get; }
        public int nativeId => m_NativeContext != null ? m_NativeContext.id : 0;
        bool m_Reported;

        /// <summary>Logs once which provider the native context ended up with (or why creation failed).</summary>
        public void ReportOnceIfReady()
        {
            if (m_Reported)
                return;
            int state = FSR4Native.GetContextState(nativeId);   // set by the render thread after the first dispatch
            if (state == 0)
                return;
            m_Reported = true;
            if (state > 0)
                Debug.Log($"[FSR4] Context active. Provider in use: {FSR4Native.GetContextVersion(nativeId)}");
            else
                Debug.LogError($"[FSR4] Native context creation failed: {FSR4Native.LastMessage()}");
        }

        public FSR4UpscalerContext(FSR4Options options, Vector2Int displayResolution) : base(displayResolution)
        {
            createdWithQuality = options.qualityMode;
            createdWithVersionOverride = FSR4Upscaler.ResolveVersionId(options);
            m_NativeContext = new FSR4NativeContext();
        }

        protected override void DestroyNativeContext(CommandBuffer cmd, FSR4NativeContext context)
            => FSR4Native.DestroyContext(cmd, context.id);

        // Quality / provider changes alter the render size or the provider, so they force a new context. Sharpening,
        // exposure and debug toggles are read every frame. (The native side also re-creates on size/flag changes.)
        protected override bool ValidateOptions(FSR4Options options)
            => options.qualityMode == createdWithQuality && FSR4Upscaler.ResolveVersionId(options) == createdWithVersionOverride;
    }

    public class FSR4Upscaler : AbstractUpscaler
    {
        public static readonly string upscalerName = "AMD FSR 4 (Custom Wrapper)";

        class PassData
        {
            public FSR4UpscalerContext context;
            public FSR4DispatchParams p;
            public TextureHandle color, depth, motionVectors, output;
            public FSR4FrameData frame;
            public bool raiseFrameHook;
            public int viewContextId;
        }

        // Best (highest) version id per FSR major generation, resolved once from the runtime's own version list so we never
        // hard-code ids. Lookup is allocation-free; it runs every frame via ValidateOptions.
        static readonly Dictionary<int, ulong> s_BestIdByMajor = new Dictionary<int, ulong>();
        static readonly HashSet<int> s_WarnedMissingMajor = new HashSet<int>();

        /// <summary>Version id to hand to FidelityFX for these options (0 = let FidelityFX choose).</summary>
        public static ulong ResolveVersionId(FSR4Options o)
        {
            if (o == null)
                return 0;
            int major;
            switch (o.provider)
            {
                case FSR4Provider.FSR4: major = 4; break;
                case FSR4Provider.FSR3: major = 3; break;
                case FSR4Provider.FSR2: major = 2; break;
                default: return o.versionOverride;
            }
            if (s_BestIdByMajor.TryGetValue(major, out ulong id))
                return id;
            if (s_WarnedMissingMajor.Add(major))
                Debug.LogWarning($"[FSR4] No FSR {major}.x provider is available here; using automatic selection instead.");
            return 0;
        }

        static void CacheVersions(List<FSR4Version> versions)
        {
            s_BestIdByMajor.Clear();
            var bestScore = new Dictionary<int, long>();
            foreach (var v in versions)
            {
                // Names look like "4.1.2" (driver-provided ones may carry a prefix and a trailing " *").
                Match m = Regex.Match(v.name, @"(\d+)\.(\d+)(?:\.(\d+))?");
                if (!m.Success)
                    continue;
                int major = int.Parse(m.Groups[1].Value);
                long score = long.Parse(m.Groups[2].Value) * 1000 + (m.Groups[3].Success ? long.Parse(m.Groups[3].Value) : 0);
                if (!bestScore.TryGetValue(major, out long cur) || score > cur)
                {
                    bestScore[major] = score;
                    s_BestIdByMajor[major] = v.id;
                }
            }
        }

        readonly bool m_Ready;
        bool m_WarnedMsaa;
        bool m_WarnedNoContext;
        static bool s_WarnedNullPtr;

        public FSR4Upscaler()
        {
            m_Ready = FSR4Native.TryInitialize(out string error);
            if (!m_Ready)
            {
                Debug.LogWarning($"[FSR4] Upscaler unavailable: {error}");
                FSR4Stats.notReadyReason = error;
                return;
            }

            int rc = FSR4Native.Probe(FSR4CreateFlags.HighDynamicRange | FSR4CreateFlags.AutoExposure, 0);
            if (rc != 0)
            {
                Debug.LogWarning($"[FSR4] Probe context creation failed (ffx code {rc}). {FSR4Native.LastMessage()}");
                FSR4Stats.notReadyReason = $"probe context creation failed (ffx code {rc})";
                m_Ready = false;
                return;
            }

            var versions = FSR4Native.GetVersions();
            CacheVersions(versions);

            if (Debug.isDebugBuild || Application.isEditor)
            {
                var sb = new System.Text.StringBuilder("[FSR4] Providers: ");
                foreach (var v in versions)
                    sb.Append($"[{v.name} = {v.id}] ");
                Debug.Log(sb.ToString());
            }
        }

        public override string name => upscalerName;
        public override bool isTemporal => true;
        public override bool supportsSharpening => true;
        public override bool hasQualityMode => true;

        // ------------------------------------------------------------------------------------------------------
        // Quality-mode math. Ratios from ffx_upscale.h (FfxApiUpscaleQualityMode).
        // ------------------------------------------------------------------------------------------------------
        public static float GetUpscaleRatio(FSR4Quality q)
        {
            switch (q)
            {
                case FSR4Quality.NativeAA:          return 1.0f;
                case FSR4Quality.Quality:           return 1.5f;
                case FSR4Quality.Balanced:          return 1.7f;
                case FSR4Quality.Performance:       return 2.0f;
                case FSR4Quality.UltraPerformance:  return 3.0f;
                default:                            return 1.5f;
            }
        }

        public override IUpscalerContext CreateContext(UpscalerOptions options, Vector2Int displayResolution)
        {
            if (!m_Ready || !(options is FSR4Options o))
                return null;
            return new FSR4UpscalerContext(o, displayResolution);
        }

        public override void CalculateJitter(int frameIndex, float upscaleRatio, out Vector2 jitter, out bool allowScaling)
        {
            // FSR phase count: 8 * ratio^2 (rounded half up - see the built-in FSR2 wrapper for why not truncated).
            int phases = Mathf.Max(1, Mathf.FloorToInt(8.0f * upscaleRatio * upscaleRatio + 0.5f));
            int haltonIndex = (frameIndex % phases) + 1;
            jitter = new Vector2(HaltonSequence.Get(haltonIndex, 2) - 0.5f, HaltonSequence.Get(haltonIndex, 3) - 0.5f);
            allowScaling = false;
        }

        public override float CalculateMipBias(Vector2Int preUpscaleResolution, Vector2Int postUpscaleResolution)
        {
            float x = Mathf.Log((float)preUpscaleResolution.x / postUpscaleResolution.x, 2f);
            float y = Mathf.Log((float)preUpscaleResolution.y / postUpscaleResolution.y, 2f);
            return Mathf.Min(x, y) - 1.0f;   // FSR guidance: log2(render/display) - 1
        }

        public override UpscalerResolutionInfo GetResolutionInfo(Vector2Int displayResolution, UpscalerOptions options)
        {
            var o = options as FSR4Options;
            if (!m_Ready || o == null)
                return UpscalerResolutionInfo.Fixed(displayResolution);

            UpscalerResolutionInfo info;
            if (o.resolutionMode == UpscalerResolutionMode.CustomScaling)
            {
                info = base.GetResolutionInfo(displayResolution, options);
            }
            else
            {
                float ratio = GetUpscaleRatio(o.qualityMode);
                // Same truncation the FidelityFX SDK uses for ffxQueryDescUpscaleGetRenderResolutionFromQualityMode.
                var render = new Vector2Int(Mathf.Max(1, (int)(displayResolution.x / ratio)),
                                            Mathf.Max(1, (int)(displayResolution.y / ratio)));
                info = UpscalerResolutionInfo.Fixed(render);
            }

            info.recommendedMinScale = 1f / GetUpscaleRatio(FSR4Quality.UltraPerformance);
            return info;
        }

        // ------------------------------------------------------------------------------------------------------
        // Render graph
        // ------------------------------------------------------------------------------------------------------
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!m_Ready)
                return;

            UpscalingIO io = frameData.Get<UpscalingIO>();

            var context = io.context as FSR4UpscalerContext;
            if (context == null || context.nativeId == 0)
            {
                FSR4Stats.MarkSkipped("no valid upscaler context");
                if (!m_WarnedNoContext)
                {
                    Debug.LogWarning("[FSR4] No valid context on UpscalingIO; skipping upscale.");
                    m_WarnedNoContext = true;
                }
                return;
            }

            context.ReportOnceIfReady();

            // FSR needs a single-sample depth buffer it can read. With MSAA on, the camera depth is multisampled.
            TextureDesc depthDesc = io.cameraDepth.GetDescriptor(renderGraph);
            if (depthDesc.msaaSamples != MSAASamples.None)
            {
                FSR4Stats.MarkSkipped("camera depth is multisampled - turn MSAA off");
                if (!m_WarnedMsaa)
                {
                    Debug.LogWarning("[FSR4] Camera depth is multisampled. Turn MSAA off in the URP asset (FSR replaces it); skipping upscale.");
                    m_WarnedMsaa = true;
                }
                return;
            }

            var options = io.options as FSR4Options;
            FSR4Stats.MarkRecorded(io.preUpscaleResolution, io.postUpscaleResolution, options, ResolveVersionId(options));
            FSR4Stats.MarkView(context.nativeId, frameData.Contains<UniversalCameraData>() ? frameData.Get<UniversalCameraData>().camera : null,
                               io.preUpscaleResolution, io.postUpscaleResolution);

            TextureHandle outputColor;
            {
                TextureDesc inDesc = io.cameraColor.GetDescriptor(renderGraph);
                TextureDesc outDesc = inDesc;
                outDesc.width = io.postUpscaleResolution.x;
                outDesc.height = io.postUpscaleResolution.y;
                outDesc.format = GraphicsFormatUtility.GetLinearFormat(inDesc.format);
                outDesc.msaaSamples = MSAASamples.None;
                outDesc.useMipMap = false;
                outDesc.autoGenerateMips = false;
                outDesc.useDynamicScale = false;
                outDesc.anisoLevel = 0;
                outDesc.discardBuffer = false;
                outDesc.enableRandomWrite = true;       // FSR writes it as a UAV
                outDesc.name = "_FSR4OutputTarget";
                outDesc.clearBuffer = false;
                outDesc.filterMode = FilterMode.Bilinear;
                outputColor = renderGraph.CreateTexture(outDesc);
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>("FSR4 Upscale", out PassData pd, new ProfilingSampler("FSR4")))
            {
                bool displayResMV = io.motionVectorTextureSize == io.postUpscaleResolution;

                var flags = FSR4CreateFlags.None;
                if (io.hdrInput)                         flags |= FSR4CreateFlags.HighDynamicRange;
                if (displayResMV)                        flags |= FSR4CreateFlags.DisplayResolutionMotionVec;
                if (io.invertedDepth)                    flags |= FSR4CreateFlags.DepthInverted;
                if (io.jitteredMotionVectors)            flags |= FSR4CreateFlags.MotionVectorJitterCancel;
                if (io.dynamicResolution.HasValue)       flags |= FSR4CreateFlags.DynamicResolution;
                if (options == null || options.autoExposure) flags |= FSR4CreateFlags.AutoExposure;
                if (options != null && options.debugChecking) flags |= FSR4CreateFlags.DebugChecking;
                bool debugView = options != null && options.debugView;
                if (debugView)                           flags |= FSR4CreateFlags.DebugVisualization;
                bool tune = options != null && options.enableTuning;

                // URP motion vectors are NDC-domain, previous->current. FSR wants "pixels from current to previous".
                float mvSign = io.motionVectorDirection == UpscalingIO.MotionVectorDirection.PreviousFrameToCurrentFrame ? -1f : 1f;
                float mvScaleX = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.x : 1f;
                float mvScaleY = io.motionVectorDomain == UpscalingIO.MotionVectorDomain.NDC ? io.motionVectorTextureSize.y : 1f;

                float jitterSign = (options != null && options.invertJitter) ? -1f : 1f;
                float frameTimeMs = io.deltaTime > 0f ? io.deltaTime * 1000f : 16.6f;

                pd.context = context;
                pd.viewContextId = context.nativeId;
                pd.p = new FSR4DispatchParams
                {
                    contextId = context.nativeId,
                    createFlags = (uint)flags,
                    versionId = ResolveVersionId(options),
                    maxRenderW = (uint)io.maxPreUpscaleResolution.x,
                    maxRenderH = (uint)io.maxPreUpscaleResolution.y,
                    displayW = (uint)io.postUpscaleResolution.x,
                    displayH = (uint)io.postUpscaleResolution.y,
                    renderW = (uint)io.preUpscaleResolution.x,
                    renderH = (uint)io.preUpscaleResolution.y,
                    dispatchFlags = debugView ? 1u : 0u,   // FFX_UPSCALE_FLAG_DRAW_DEBUG_VIEW
                    enableSharpening = (options != null && options.enableSharpening) ? 1 : 0,
                    sharpness = options != null ? options.sharpness : 0f,
                    reset = io.resetHistory ? 1 : 0,
                    jitterX = io.subpixelJitter.x * jitterSign,
                    jitterY = io.subpixelJitter.y * jitterSign,
                    mvScaleX = mvSign * mvScaleX,
                    mvScaleY = mvSign * mvScaleY,
                    frameTimeMs = frameTimeMs,
                    preExposure = 1.0f,
                    cameraNear = io.nearClipPlane,
                    cameraFar = io.farClipPlane,
                    cameraFovY = io.fieldOfViewDegrees * Mathf.Deg2Rad,
                    viewSpaceToMeters = 1.0f,
                    tuningMask = tune ? 0x1F : 0,
                    tuning0 = tune ? options.velocityFactor : 1f,
                    tuning1 = tune ? options.reactivenessScale : 1f,
                    tuning2 = tune ? options.shadingChangeScale : 1f,
                    tuning3 = tune ? options.accumulationAddedPerFrame : 1f / 3f,
                    tuning4 = tune ? options.minDisocclusionAccumulation : -1f / 3f,
                };

                // Optional modules (frame generation) get the same inputs through FSR4Hooks.UpscaleDispatched.
                pd.raiseFrameHook = FSR4Hooks.hasUpscaleListeners;
                if (pd.raiseFrameHook)
                {
                    Vector3 camPos = Vector3.zero, camUp = Vector3.up, camRight = Vector3.right, camFwd = Vector3.forward;
                    Camera cam = null;
                    if (frameData.Contains<UniversalCameraData>())
                    {
                        cam = frameData.Get<UniversalCameraData>().camera;
                        Transform camT = cam.transform;
                        camPos = camT.position; camUp = camT.up; camRight = camT.right; camFwd = camT.forward;
                    }
                    pd.frame = new FSR4FrameData
                    {
                        renderResolution = io.preUpscaleResolution,
                        maxRenderResolution = io.maxPreUpscaleResolution,
                        displayResolution = io.postUpscaleResolution,
                        jitter = new Vector2(pd.p.jitterX, pd.p.jitterY),
                        motionVectorScale = new Vector2(pd.p.mvScaleX, pd.p.mvScaleY),
                        frameTimeMs = frameTimeMs,
                        cameraNear = io.nearClipPlane,
                        cameraFar = io.farClipPlane,
                        cameraFovY = io.fieldOfViewDegrees * Mathf.Deg2Rad,
                        resetHistory = io.resetHistory,
                        depthInverted = io.invertedDepth,
                        displayResolutionMotionVectors = displayResMV,
                        motionVectorJitterCancelled = io.jitteredMotionVectors,
                        cameraPosition = camPos, cameraUp = camUp, cameraRight = camRight, cameraForward = camFwd,
                        camera = cam,
                    };
                }

                builder.UseTexture(io.cameraColor);
                builder.UseTexture(io.cameraDepth);
                builder.UseTexture(io.motionVectorColor);
                builder.UseTexture(outputColor, AccessFlags.Write);
                pd.color = io.cameraColor;
                pd.depth = io.cameraDepth;
                pd.motionVectors = io.motionVectorColor;
                pd.output = outputColor;

                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, UnsafeGraphContext ctx) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);

                    // Native pointers are fetched here, at execute time, because render-graph textures only have
                    // backing RTs while the graph runs.
                    d.p.color = NativePtr(d.color);
                    d.p.depth = NativePtr(d.depth);
                    d.p.motionVectors = NativePtr(d.motionVectors);
                    d.p.output = NativePtr(d.output);

                    if (d.p.color == IntPtr.Zero || d.p.depth == IntPtr.Zero ||
                        d.p.motionVectors == IntPtr.Zero || d.p.output == IntPtr.Zero)
                    {
                        FSR4Stats.MarkSkipped("a pass texture had no native resource");
                        if (!s_WarnedNullPtr)
                        {
                            Debug.LogWarning("[FSR4] A pass texture had no native resource; skipping dispatch.");
                            s_WarnedNullPtr = true;
                        }
                        return;
                    }

                    FSR4Native.Dispatch(cmd, d.p);
                    FSR4Stats.dispatchesIssued++;
                    FSR4Stats.MarkViewDispatched(d.viewContextId);

                    if (d.raiseFrameHook)
                    {
                        d.frame.depth = d.p.depth;
                        d.frame.motionVectors = d.p.motionVectors;
                        d.frame.color = d.p.output;
                        FSR4Hooks.RaiseUpscaleDispatched(cmd, in d.frame);
                    }
                });
            }

            io.cameraColor = outputColor;
        }

        static IntPtr NativePtr(TextureHandle handle)
        {
            RTHandle rth = handle;
            return rth != null && rth.rt != null ? rth.rt.GetNativeTexturePtr() : IntPtr.Zero;
        }
    }
}
#endif
