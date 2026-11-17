using System;
using UnityEngine;

namespace FSRShowcase
{
    public enum LegacyUpscaler { STP = 0, FSR1Spatial = 1, Bilinear = 2, Point = 3 }
    public enum FsrGeneration { Auto = 0, FSR4 = 1, FSR3 = 2, FSR2 = 3 }
    public enum HudTestKind { Off = 0, OverlayUIToolkit = 1, ScreenSpaceCamera = 2, WorldSpace = 3 }

    /// <summary>Everything the in-game graphics menu edits. Serialised to PlayerPrefs as JSON.</summary>
    [Serializable]
    public class ShowcaseSettings
    {
        // --- Display ---
        public int width = 1920;
        public int height = 1080;
        public int displayMode;          // 0 borderless, 1 exclusive fullscreen, 2 windowed
        public int vsync;                // 0 off, 1 on
        public int frameCap;             // 0 = unlimited
        public float fov = 60f;

        // --- Upscaling ---
        public bool upscalingEnabled = true;
        public bool legacyUpscalers;
        public LegacyUpscaler legacyKind = LegacyUpscaler.STP;
        public FsrGeneration fsrGeneration = FsrGeneration.Auto;
        public int quality = 1;          // 0 Native AA, 1 Quality, 2 Balanced, 3 Performance, 4 Ultra Performance
        public float renderScale = 0.67f; // only for legacy upscalers
        public bool sharpening;
        public float sharpness = 0.5f;

        // --- Frame generation ---
        public bool frameGen;
        public bool pacing = true;
        public bool hudless;

        // --- Advanced graphics ---
        public int preset = 3;           // 0 low .. 3 ultra, 4 custom
        public int shadowQuality = 3;    // 0 off, 1 low, 2 medium, 3 high, 4 ultra
        public float shadowDistance = 80f;
        public int textureQuality = 0;   // 0 full, 1 half, 2 quarter
        public int anisotropic = 4;      // 0 off, 1 2x, 2 4x, 3 8x, 4 16x
        public float lodBias = 1f;
        public bool softShadows = true;
        public bool postProcessing = true;
        public bool hdr = true;

        // --- Overlays ---
        public bool showPerfOverlay = true;
        public bool showDebugHud;
        public HudTestKind hudTest = HudTestKind.Off;

        const string Key = "fsrshowcase.settings.v1";

        public ShowcaseSettings Clone() => JsonUtility.FromJson<ShowcaseSettings>(JsonUtility.ToJson(this));

        public static ShowcaseSettings Load()
        {
            try
            {
                if (PlayerPrefs.HasKey(Key))
                    return JsonUtility.FromJson<ShowcaseSettings>(PlayerPrefs.GetString(Key));
            }
            catch (Exception e) { Debug.LogWarning("[Showcase] Could not load settings: " + e.Message); }
            return new ShowcaseSettings();
        }

        public void Save()
        {
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(this));
            PlayerPrefs.Save();
        }

        /// <summary>Fills the advanced block from a named preset (0 low .. 3 ultra).</summary>
        public void ApplyPreset(int p)
        {
            preset = p;
            switch (p)
            {
                case 0: shadowQuality = 1; shadowDistance = 30f;  textureQuality = 2; anisotropic = 1; lodBias = 0.6f; softShadows = false; postProcessing = false; break;
                case 1: shadowQuality = 2; shadowDistance = 50f;  textureQuality = 1; anisotropic = 2; lodBias = 0.8f; softShadows = false; postProcessing = true;  break;
                case 2: shadowQuality = 3; shadowDistance = 70f;  textureQuality = 0; anisotropic = 3; lodBias = 1.0f; softShadows = true;  postProcessing = true;  break;
                default: shadowQuality = 4; shadowDistance = 100f; textureQuality = 0; anisotropic = 4; lodBias = 1.5f; softShadows = true; postProcessing = true; break;
            }
        }
    }
}
