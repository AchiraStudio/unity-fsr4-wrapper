using AchiraStudio.FSR4;
using AchiraStudio.FSR4.DebugHud;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FSRShowcase
{
    /// <summary>Applies <see cref="ShowcaseSettings"/> to Unity, URP and the FSR wrapper. All runtime graphics changes go through here.</summary>
    public static class GraphicsController
    {
        public const string WrapperId = "achirastudio.fsr4";

        /// <summary>Shader loop iterations at 100% synthetic GPU load.</summary>
        public const int GpuLoadMaxIterations = 4000;

        public static UniversalRenderPipelineAsset Asset => GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        public static UniversalRenderPipeline Pipeline => RenderPipelineManager.currentPipeline as UniversalRenderPipeline;
        public static FSR4Options WrapperOptions => Asset != null ? Asset.GetUpscalerOptions(WrapperId) as FSR4Options : null;

        public static string LegacyId(LegacyUpscaler k)
        {
            switch (k)
            {
                case LegacyUpscaler.STP: return "unity.stp";
                case LegacyUpscaler.FSR1Spatial: return "amd.fsr1";
                case LegacyUpscaler.Bilinear: return "unity.bilinear";
                default: return "unity.point";
            }
        }

        /// <summary>True when the wrapper is the upscaler the pipeline is using right now.</summary>
        public static bool WrapperActive => Pipeline != null && Pipeline.activeUpscalerName == FSR4Upscaler.upscalerName;

        public static void ApplyDisplay(ShowcaseSettings s)
        {
            var mode = s.displayMode == 1 ? FullScreenMode.ExclusiveFullScreen
                     : s.displayMode == 2 ? FullScreenMode.Windowed : FullScreenMode.FullScreenWindow;
            if (Screen.width != s.width || Screen.height != s.height || Screen.fullScreenMode != mode)
                Screen.SetResolution(s.width, s.height, mode);
        }

        public static void Apply(ShowcaseSettings s, bool includeDisplay = true)
        {
            if (includeDisplay)
                ApplyDisplay(s);

            // Frame generation needs VSync off on Direct3D 12, so it wins over the VSync setting.
            bool wantFrameGen = s.frameGen && s.upscalingEnabled && !s.legacyUpscalers;
            QualitySettings.vSyncCount = (s.vsync == 1 && !wantFrameGen) ? 1 : 0;
            Application.targetFrameRate = s.frameCap <= 0 ? -1 : s.frameCap;

            GpuLoadFeature.Iterations = Mathf.RoundToInt(Mathf.Max(0f, s.gpuLoad) * GpuLoadMaxIterations);   // the menu slider stops at 1, scripts may go higher
            ApplyUpscaling(s);
            ApplyFrameGeneration(s, wantFrameGen);
            ApplyAdvanced(s);

            var cam = Camera.main;
            if (cam != null) cam.fieldOfView = s.fov;
        }

        static void ApplyUpscaling(ShowcaseSettings s)
        {
            var pipeline = Pipeline;
            var asset = Asset;
            if (pipeline == null || asset == null)
                return;

            if (!s.upscalingEnabled)
            {
                pipeline.SetUpscaler("unity.auto");
                asset.renderScale = 1f;
                return;
            }

            if (s.legacyUpscalers)
            {
                pipeline.SetUpscaler(LegacyId(s.legacyKind));
                asset.renderScale = Mathf.Clamp(s.renderScale, 0.33f, 1f);
                return;
            }

            if (!string.IsNullOrEmpty(FSR4Stats.notReadyReason))
            {
                // The native plugin could not start (for example the Editor runs on Direct3D 11). Use STP at the same render scale
                // instead of leaving the image point-filtered; the menu's status rows still show the reason.
                pipeline.SetUpscaler("unity.stp");
                asset.renderScale = 1f / FSR4Upscaler.GetUpscaleRatio((FSR4Quality)Mathf.Clamp(s.quality, 0, 4));
                return;
            }

            pipeline.SetUpscaler(WrapperId);
            asset.renderScale = 1f;
            var o = WrapperOptions;
            if (o == null)
                return;
            o.resolutionMode = UpscalerResolutionMode.QualityMode;
            o.qualityMode = (FSR4Quality)Mathf.Clamp(s.quality, 0, 4);
            o.provider = (FSR4Provider)(int)s.fsrGeneration;
            o.enableSharpening = s.sharpening;
            o.sharpness = s.sharpness;
        }

        static void ApplyFrameGeneration(ShowcaseSettings s, bool want)
        {
            FSR4FrameGeneration.pacing = s.pacing;
            FSR4FrameGeneration.hudless = s.hudless;
            FSR4FrameGeneration.enabled = want;
        }

        static void ApplyAdvanced(ShowcaseSettings s)
        {
            var asset = Asset;
            if (asset != null)
            {
                int[] res = { 0, 512, 1024, 2048, 4096 };
                asset.mainLightShadowmapResolution = res[Mathf.Clamp(s.shadowQuality, 1, 4)];
                asset.shadowDistance = s.shadowDistance;
                asset.shadowCascadeCount = s.shadowQuality >= 3 ? 4 : (s.shadowQuality == 2 ? 3 : 2);
                asset.supportsHDR = s.hdr;
            }

            // Soft/hard filtering is a per-light setting in URP 17.7 (the asset only exposes it read-only).
            var sun = RenderSettings.sun;
            if (sun != null && s.shadowQuality > 0)
                sun.shadows = s.softShadows ? LightShadows.Soft : LightShadows.Hard;

            QualitySettings.globalTextureMipmapLimit = Mathf.Clamp(s.textureQuality, 0, 3);
            QualitySettings.anisotropicFiltering = s.anisotropic == 0 ? AnisotropicFiltering.Disable : AnisotropicFiltering.ForceEnable;
            int[] aniso = { 1, 2, 4, 8, 16 };
            Texture.SetGlobalAnisotropicFilteringLimits(1, aniso[Mathf.Clamp(s.anisotropic, 0, 4)]);
            QualitySettings.lodBias = s.lodBias;

            foreach (var cam in Camera.allCameras)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                if (data == null) continue;
                data.renderPostProcessing = s.postProcessing;
                data.renderShadows = s.shadowQuality > 0;
            }
        }

        // -------- read-outs ---------------------------------------------------------------------------------------------

        public static string ActiveUpscalerName => Pipeline != null ? Pipeline.activeUpscalerName : "-";

        public static string DescribeActive()
        {
            if (WrapperActive)
            {
                string p = FSR4Diagnostics.ProviderInUse();
                return $"{(string.IsNullOrEmpty(p) ? "FSR (starting)" : "FSR " + p)}";
            }
            return ActiveUpscalerName;
        }
    }
}
