#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4
{
    /// <summary>Per-ratio presets of FSR (per-dimension upscale ratios from ffx_upscale.h).</summary>
    public enum FSR4Quality
    {
        NativeAA = 0,          // 1.0x
        Quality = 1,           // 1.5x
        Balanced = 2,          // 1.7x
        Performance = 3,       // 2.0x
        UltraPerformance = 4,  // 3.0x
    }

    /// <summary>Which FidelityFX upscaler generation to run. Auto lets FidelityFX pick the newest one the GPU supports.</summary>
    public enum FSR4Provider
    {
        Auto = 0,   // newest available (FSR 4 ML on supported AMD GPUs, otherwise FSR 3.1)
        FSR4 = 1,   // FSR 4 (ML) - falls back to Auto if this machine can't run it
        FSR3 = 2,   // FSR 3.1 (analytical, runs on any DX12 GPU)
        FSR2 = 3,   // FSR 2.3 (analytical)
    }

    /// <summary>
    /// Options asset for the custom FSR4 upscaler. The framework stores one instance as a sub-asset of the URP asset.
    /// </summary>
    [Serializable]
    public class FSR4Options : UpscalerOptions
    {
        [SerializeField, Tooltip("Render-resolution preset used when Resolution Mode is 'Quality Mode'.")]
        FSR4Quality m_QualityMode = FSR4Quality.Quality;

        [SerializeField, Tooltip("Which FSR generation to use. Auto = newest the GPU can run. Pick FSR 3 to test or ship the " +
                                 "analytical version even on GPUs that support FSR 4.")]
        FSR4Provider m_Provider = FSR4Provider.Auto;

        [SerializeField, Tooltip("Advanced: pin an exact FidelityFX version id (0 = none). Only used when Provider is Auto. " +
                                 "Ids are logged once at startup and can differ between machines.")]
        ulong m_VersionOverride = 0;

        [SerializeField, Tooltip("Let FSR compute exposure itself. URP does not provide an exposure texture, so leave this on.")]
        bool m_AutoExposure = true;

        [SerializeField, Tooltip("Optional sharpening pass after upscaling.")]
        bool m_EnableSharpening = false;

        [SerializeField, Range(0f, 1f)]
        float m_Sharpness = 0.5f;

        [SerializeField, Tooltip("Ask the FidelityFX runtime to validate inputs and report problems (slower; for bring-up).")]
        bool m_DebugChecking = false;

        [SerializeField, Tooltip("Bring-up toggle: flips the sign of the jitter passed to FSR. Try it if the image shimmers or looks " +
                                 "doubled while the camera is still.")]
        bool m_InvertJitter = false;

        [SerializeField, Tooltip("Draws FSR's own debug view into the output (reactive/disocclusion/accumulation info). Allocates extra memory.")]
        bool m_DebugView = false;

        [SerializeField, Tooltip("Apply the tuning values below (FSR 3.1/2.x ignore keys they do not know; FSR 4 ML may ignore several).")]
        bool m_EnableTuning = false;

        [SerializeField, Range(0f, 1f), Tooltip("fVelocityFactor. 0 can improve temporal stability of bright pixels. Default 1.")]
        float m_VelocityFactor = 1f;

        [SerializeField, Min(0f), Tooltip("fReactivenessScale. Larger = reactive mask reduces ghosting more. Default 1.")]
        float m_ReactivenessScale = 1f;

        [SerializeField, Min(0f), Tooltip("fShadingChangeScale. Larger = more reactive to shading changes. Default 1.")]
        float m_ShadingChangeScale = 1f;

        [SerializeField, Range(0f, 1f), Tooltip("fAccumulationAddedPerFrame. Lower = less ghosting, more thin-feature flicker. Default 0.333.")]
        float m_AccumulationAddedPerFrame = 1f / 3f;

        [SerializeField, Range(-1f, 1f), Tooltip("fMinDisocclusionAccumulation. Higher may reduce flicker around swaying thin objects. Default -0.333.")]
        float m_MinDisocclusionAccumulation = -1f / 3f;

        public FSR4Quality qualityMode { get => m_QualityMode; set => m_QualityMode = value; }
        public FSR4Provider provider { get => m_Provider; set => m_Provider = value; }
        public ulong versionOverride { get => m_VersionOverride; set => m_VersionOverride = value; }
        public bool autoExposure { get => m_AutoExposure; set => m_AutoExposure = value; }
        public bool enableSharpening { get => m_EnableSharpening; set => m_EnableSharpening = value; }
        public float sharpness { get => m_Sharpness; set => m_Sharpness = value; }
        public bool debugChecking { get => m_DebugChecking; set => m_DebugChecking = value; }
        public bool invertJitter { get => m_InvertJitter; set => m_InvertJitter = value; }
        public bool debugView { get => m_DebugView; set => m_DebugView = value; }
        public bool enableTuning { get => m_EnableTuning; set => m_EnableTuning = value; }
        public float velocityFactor { get => m_VelocityFactor; set => m_VelocityFactor = value; }
        public float reactivenessScale { get => m_ReactivenessScale; set => m_ReactivenessScale = value; }
        public float shadingChangeScale { get => m_ShadingChangeScale; set => m_ShadingChangeScale = value; }
        public float accumulationAddedPerFrame { get => m_AccumulationAddedPerFrame; set => m_AccumulationAddedPerFrame = value; }
        public float minDisocclusionAccumulation { get => m_MinDisocclusionAccumulation; set => m_MinDisocclusionAccumulation = value; }

        public void CopyFrom(FSR4Options other)
        {
            if (other == null) return;
            resolutionMode = other.resolutionMode;
            m_QualityMode = other.m_QualityMode;
            m_Provider = other.m_Provider;
            m_VersionOverride = other.m_VersionOverride;
            m_AutoExposure = other.m_AutoExposure;
            m_EnableSharpening = other.m_EnableSharpening;
            m_Sharpness = other.m_Sharpness;
            m_DebugChecking = other.m_DebugChecking;
            m_InvertJitter = other.m_InvertJitter;
            m_DebugView = other.m_DebugView;
            m_EnableTuning = other.m_EnableTuning;
            m_VelocityFactor = other.m_VelocityFactor;
            m_ReactivenessScale = other.m_ReactivenessScale;
            m_ShadingChangeScale = other.m_ShadingChangeScale;
            m_AccumulationAddedPerFrame = other.m_AccumulationAddedPerFrame;
            m_MinDisocclusionAccumulation = other.m_MinDisocclusionAccumulation;
        }
    }
}
#endif
