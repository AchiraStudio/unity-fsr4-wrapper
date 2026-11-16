#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>
    /// Optional scene component that applies frame-generation settings while it is enabled. You can also drive
    /// <see cref="FSR4FrameGeneration"/> from your own settings code instead.
    /// </summary>
    [AddComponentMenu("AchiraStudio/FSR4 Frame Generation Controller")]
    [DisallowMultipleComponent]
    public sealed class FSR4FrameGenerationController : MonoBehaviour
    {
        [SerializeField, Tooltip("Experimental. Works in Player builds on Direct3D12 / Vulkan only.")]
        bool m_EnableFrameGeneration = true;

        [SerializeField, Tooltip("Wait half a frame interval between the generated and the real frame when VSync does not already pace them.")]
        bool m_Pacing = true;

        [SerializeField, Tooltip("Overlays drawn into the generated frame, for diagnosing artefacts.")]
        FSR4FrameGenDebug m_Debug = FSR4FrameGenDebug.None;

        void OnEnable() => Apply();
        void OnValidate() { if (isActiveAndEnabled) Apply(); }
        void OnDisable() => FSR4FrameGeneration.enabled = false;

        void Apply()
        {
            FSR4FrameGeneration.pacing = m_Pacing;
            FSR4FrameGeneration.debug = m_Debug;
            FSR4FrameGeneration.enabled = m_EnableFrameGeneration;
        }
    }
}
#endif
