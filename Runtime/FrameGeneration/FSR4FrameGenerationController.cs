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

        [SerializeField, Tooltip("Experimental. Gives frame generation an image without the screen-space UI to reduce UI ghosting. Needs the " +
                                 "FSR4 HUD-less Capture renderer feature on the URP renderer, Direct3D12 and SDR output.")]
        bool m_HudlessInput = false;

        [SerializeField, Tooltip("Pause frame generation while two or more cameras share the screen (split-screen). It interpolates the whole " +
                                 "screen from one camera's data, which is wrong for the other views.")]
        bool m_PauseInSplitScreen = true;

        // Only the controller that is currently applying its settings may switch frame generation off again. Without this,
        // a second controller (for example one in a scene that is unloaded) switched it off for the one that stayed.
        static FSR4FrameGenerationController s_Owner;

        void OnEnable() => Apply();
        void OnValidate() { if (isActiveAndEnabled) Apply(); }

        void OnDisable()
        {
            if (s_Owner != this)
                return;
            s_Owner = null;
            FSR4FrameGeneration.enabled = false;
            FSR4FrameGeneration.hudless = false;
        }

        void Apply()
        {
            s_Owner = this;
            FSR4FrameGeneration.pacing = m_Pacing;
            FSR4FrameGeneration.debug = m_Debug;
            FSR4FrameGeneration.hudless = m_HudlessInput;
            FSR4FrameGeneration.pauseInSplitScreen = m_PauseInSplitScreen;
            FSR4FrameGeneration.enabled = m_EnableFrameGeneration;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Owner = null;
    }
}
#endif
