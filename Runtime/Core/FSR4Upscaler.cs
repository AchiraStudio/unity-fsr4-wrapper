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
        static RegisterFSR4() => FSR4Upscaler.RegisterWithFramework();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void InitRuntime()
        {
            FSR4Upscaler.ResetRuntimeState();
            FSR4Upscaler.RegisterWithFramework();
        }
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

        // Dispatch watchdog bookkeeping (see FSR4Upscaler.WatchDispatches).
        internal int watchFrames;
        internal ulong watchAttempts, watchExecuted;

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

        /// <summary>
        /// Stable identifier serialized by the URP asset. Used by the Upscaler Framework in URP 17.7 (Unity 6.7) and newer;
        /// never change it once shipped.
        /// </summary>
        public static readonly string upscalerIdentifier = "achirastudio.fsr4";

        /// <summary>
        /// Registers the upscaler with the framework. Unity 6.6 (URP 17.6) registers by display name only; Unity 6.7
        /// (URP 17.7) registers by stable id plus display name and requires <c>AbstractUpscaler.upscalerId</c>.
        /// <c>FSR4_UPSCALER_ID_API</c> is defined by this assembly's versionDefines for com.unity.render-pipelines.core >= 17.7.0.
        /// </summary>
        internal static void RegisterWithFramework()
        {
#if FSR4_UPSCALER_ID_API
            UpscalerRegistry.Register<FSR4Upscaler, FSR4Options>(upscalerIdentifier, upscalerName);
#else
            UpscalerRegistry.Register<FSR4Upscaler, FSR4Options>(upscalerName);
#endif
        }

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

        // ------------------------------------------------------------------------------------------------------
        // Failure handling. A FidelityFX context can fail to exist on a machine that looked fine at startup: the chosen provider
        // may not run on this GPU (FSR 4 needs a Radeon RX 9000 series), the driver may refuse the exact size / flags, or a dispatch
        // may fail every frame. The native side only finds out on the render thread, after the output texture was already handed to
        // the rest of the frame, so an unhandled failure shows up as an uninitialised (often pink or black) image that never
        // recovers. These pieces make that impossible:
        //   1. every provider id is probed once with a synchronous throw-away context before it is first used,
        //   2. a provider that fails (at the probe, at creation, or by failing every dispatch) is blocked and the next one in the
        //      chain is used: FSR 4 > FSR 3.1 > FSR 2.3 > automatic,
        //   3. a new context's output is only used once the render thread has confirmed the context exists,
        //   4. when nothing works the upscaler reports itself unsupported and records no pass, so URP shows its own (un-upscaled)
        //      image instead of FSR's unwritten output.
        // ------------------------------------------------------------------------------------------------------
        const ulong kSimulatedBadId = 0xFFFF_0000_DEAD_BEEFul;
        static readonly HashSet<ulong> s_BlockedIds = new HashSet<ulong>();
        static readonly Dictionary<ulong, bool> s_ProbeResults = new Dictionary<ulong, bool>();
        static readonly HashSet<ulong> s_WarnedBlocked = new HashSet<ulong>();
        static int s_StateVersion;
        static FSR4Provider s_CacheProvider;
        static ulong s_CacheOverride;
        static int s_CacheStateVersion = -1;
        static ulong s_CacheResult;
        static bool s_NativeReady;
        static bool s_Failed;
        static string s_FailureReason = "";
        static int s_Simulate;
        static bool s_SimulateProbeFailedOnce;

        /// <summary>
        /// Non-empty once every FSR provider has failed on this machine. The upscaler then reports itself unsupported
        /// (URP 17.7 moves on to the next entry of its priority list) and records no pass.
        /// </summary>
        public static string runtimeFailureReason => s_FailureReason;

        /// <summary>True when every FSR provider has failed on this machine (see <see cref="runtimeFailureReason"/>).</summary>
        public static bool hasFailed => s_Failed;

        /// <summary>Raised on the main thread when the upscaler gives up on this machine. The argument is the reason.</summary>
        public static event Action<string> RuntimeFailed;

        /// <summary>Raised on the main thread when one provider failed and the next one in the chain is used. Arguments: failed provider name, reason.</summary>
        public static event Action<string, string> ProviderFailed;

        internal static void ResetRuntimeState()
        {
            // Domain reload can be disabled in the Editor, so statics survive between play sessions.
            s_BlockedIds.Clear(); s_ProbeResults.Clear(); s_WarnedBlocked.Clear();
            s_StateVersion++; s_CacheStateVersion = -1;
            s_Failed = false; s_FailureReason = ""; s_NativeReady = false; s_SimulateProbeFailedOnce = false;
            s_Simulate = 0;
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                // -fsr4-simulate-failure=first|probe|all : see the README, "Testing the fallback path".
                if (arg.StartsWith("-fsr4-simulate-failure", StringComparison.OrdinalIgnoreCase))
                {
                    string mode = arg.Length > 22 && arg[22] == '=' ? arg.Substring(23).ToLowerInvariant() : "render";
                    s_Simulate = mode == "probe" ? 1 : mode == "all" ? 2 : 3;
                }
            }
            if (s_Simulate != 0)
                Debug.LogWarning($"[FSR4] Failure simulation is ON (mode {s_Simulate}: 1 = first provider fails its probe, 2 = every provider fails, 3 = first provider fails on the render thread).");
        }

        static string NameOf(ulong id)
        {
            if (id == kSimulatedBadId) return "(simulated bad provider)";
            if (id == 0) return "automatic";
            foreach (var kv in s_BestIdByMajor)
                if (kv.Value == id) return "FSR " + kv.Key + ".x";
            return "provider " + id;
        }

        static void BlockProvider(ulong id, string reason)
        {
            if (!s_BlockedIds.Add(id))
                return;
            s_StateVersion++;
            if (s_WarnedBlocked.Add(id))
                Debug.LogWarning($"[FSR4] {NameOf(id)} failed on this machine ({reason}). Switching to the next provider.");
            ProviderFailed?.Invoke(NameOf(id), reason);
        }

        static void FailForGood(string reason)
        {
            if (s_Failed)
                return;
            s_Failed = true;
            s_FailureReason = reason;
            FSR4Stats.notReadyReason = reason;
            Debug.LogError($"[FSR4] No FSR provider works on this machine: {reason}. The upscaler is disabled; the frame is shown without FSR.");
            RuntimeFailed?.Invoke(reason);
        }

        // The version a candidate slot holds, or false when the runtime does not list that generation.
        static bool TryGetCandidate(FSR4Provider provider, ulong versionOverride, int slot, out ulong id)
        {
            id = 0;
            switch (provider)
            {
                case FSR4Provider.FSR4: return slot == 0 ? Best(4, out id) : slot == 1 ? Best(3, out id) : slot == 2 ? Best(2, out id) : slot == 3;
                case FSR4Provider.FSR3: return slot == 0 ? Best(3, out id) : slot == 1 ? Best(2, out id) : slot == 2;
                case FSR4Provider.FSR2: return slot == 0 ? Best(2, out id) : slot == 1;
                default:                // Auto: let FidelityFX choose (or the pinned version id), then the analytical providers
                    if (slot == 0) { id = versionOverride; return true; }
                    return slot == 1 ? Best(3, out id) : slot == 2 && Best(2, out id);
            }
        }

        static bool Best(int major, out ulong id)
        {
            if (s_BestIdByMajor.TryGetValue(major, out id))
                return true;
            if (s_WarnedMissingMajor.Add(major))
                Debug.Log($"[FSR4] No FSR {major}.x provider is listed on this machine; skipping it.");
            return false;
        }

        static bool PassesProbe(ulong id)
        {
            if (!s_NativeReady)
                return true;
            if (s_Simulate == 2 || (s_Simulate == 1 && !s_SimulateProbeFailedOnce))
            {
                s_SimulateProbeFailedOnce = true;
                return false;
            }
            if (s_ProbeResults.TryGetValue(id, out bool ok))
                return ok;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int rc = FSR4Native.Probe(FSR4CreateFlags.HighDynamicRange | FSR4CreateFlags.AutoExposure, id);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            ok = rc == 0;
            s_ProbeResults[id] = ok;
            Debug.Log(ok ? $"[FSR4] Probed {NameOf(id)}: OK ({ms:0} ms)"
                         : $"[FSR4] Probed {NameOf(id)}: FAILED, ffx code {rc} ({ms:0} ms). {FSR4Native.LastMessage()}");
            return ok;
        }

        static ulong ChooseVersionId(FSR4Provider provider, ulong versionOverride)
        {
            if (!s_NativeReady)
                return versionOverride;
            if (s_Failed)
                return 0;

            if (s_Simulate == 3 && !s_BlockedIds.Contains(kSimulatedBadId))
                return kSimulatedBadId;   // passes no probe: the context fails on the render thread, like a real late failure

            for (int slot = 0; slot < 4; slot++)
            {
                if (!TryGetCandidate(provider, versionOverride, slot, out ulong id))
                    continue;
                if (s_BlockedIds.Contains(id))
                    continue;
                if (!PassesProbe(id))
                {
                    s_BlockedIds.Add(id);
                    s_StateVersion++;
                    ProviderFailed?.Invoke(NameOf(id), "probe failed");
                    continue;
                }
                return id;
            }
            string last = FSR4Native.LastMessage();
            FailForGood(string.IsNullOrEmpty(last) ? "every FSR provider failed its pre-flight check" : "every FSR provider failed (" + last + ")");
            return 0;
        }

        /// <summary>
        /// Version id to hand to FidelityFX for these options (0 = let FidelityFX choose). Follows the failover chain
        /// FSR 4 &gt; FSR 3.1 &gt; FSR 2.3 &gt; automatic, skipping providers that failed on this machine.
        /// </summary>
        public static ulong ResolveVersionId(FSR4Options o)
        {
            if (o == null)
                return 0;
            if (s_CacheStateVersion == s_StateVersion && s_CacheProvider == o.provider && s_CacheOverride == o.versionOverride)
                return s_CacheResult;
            ulong id = ChooseVersionId(o.provider, o.versionOverride);
            s_CacheProvider = o.provider; s_CacheOverride = o.versionOverride;
            s_CacheStateVersion = s_StateVersion; s_CacheResult = id;
            return id;
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
            s_NativeReady = true;
            s_ProbeResults[0] = true;          // the probe above was the automatic selection
            s_StateVersion++;

            if (Debug.isDebugBuild || Application.isEditor)
            {
                var sb = new System.Text.StringBuilder("[FSR4] Providers: ");
                foreach (var v in versions)
                    sb.Append($"[{v.name} = {v.id}] ");
                Debug.Log(sb.ToString());
            }
        }

#if FSR4_UPSCALER_ID_API
        public override string upscalerId => upscalerIdentifier;

        // URP 17.7 walks the asset's upscaler priority list and picks the first upscaler that reports itself supported.
        // Without this, a machine where the native plugin cannot start (Direct3D 11 Editor, missing DLL) would stay on this
        // upscaler and render a degraded, point-filtered image instead of falling back to the next entry (for example STP).
        public override bool isSupportedOnDevice => m_Ready && !s_Failed;
#endif
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
            if (!m_Ready || s_Failed || !(options is FSR4Options o))
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
        /// <summary>
        /// A context whose dispatches keep failing (native ffxDispatch error: unsupported resource format, device trouble) never
        /// writes the output texture. The native side counts dispatch events that reached the render thread and the ones that were
        /// submitted; if, over a window of frames, none were submitted although many were attempted, the provider is blocked.
        /// </summary>
        void WatchDispatches(FSR4UpscalerContext context)
        {
            if (++context.watchFrames < 90)
                return;
            context.watchFrames = 0;
            if (!FSR4Native.TryGetDispatchCounts(out ulong attempts, out ulong executed))
                return;
            bool stuck = attempts - context.watchAttempts >= 30 && executed == context.watchExecuted && context.watchAttempts != 0;
            context.watchAttempts = attempts;
            context.watchExecuted = executed;
            if (stuck)
                BlockProvider(context.createdWithVersionOverride, "every dispatch failed: " + FSR4Native.LastMessage());
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!m_Ready || s_Failed)
            {
                if (s_Failed)
                    FSR4Stats.MarkSkipped("disabled: " + s_FailureReason);
                return;
            }

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

            // The render thread creates the native context lazily on the first dispatch, so its state is 0 for the first frame
            // or two, then 1 (works) or -1 (failed). On failure the provider is blocked and the next frame's ValidateOptions
            // sees a different version id, which makes the framework build a new context with the next provider in the chain.
            int contextState = FSR4Native.GetContextState(context.nativeId);
            if (contextState < 0)
            {
                string why = FSR4Native.LastMessage();
                FSR4Stats.MarkSkipped("native context creation failed (" + why + "); switching provider");
                BlockProvider(context.createdWithVersionOverride, why);
                ResolveVersionId(io.options as FSR4Options);   // may declare the upscaler unusable
                return;
            }
            if (contextState > 0)
                WatchDispatches(context);

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

            // Until the render thread confirmed that the context exists (state 1) the output texture may be unwritten, so the
            // frame keeps using the un-upscaled colour. Normally that is one or two frames after a context is created.
            if (contextState > 0)
                io.cameraColor = outputColor;
            else
                FSR4Stats.MarkSkipped("warming up: waiting for the native context");
        }

        static IntPtr NativePtr(TextureHandle handle)
        {
            RTHandle rth = handle;
            return rth != null && rth.rt != null ? rth.rt.GetNativeTexturePtr() : IntPtr.Zero;
        }
    }
}
#endif
