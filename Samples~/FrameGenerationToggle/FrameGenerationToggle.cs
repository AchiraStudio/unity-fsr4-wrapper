#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;
using AchiraStudio.FSR4.FrameGeneration;

/// <summary>
/// Sample: toggle frame generation from your own code (e.g. a settings menu). Add to any GameObject.
/// Frame generation is experimental and only works in Player builds (Direct3D12 / Vulkan).
/// </summary>
public class FrameGenerationToggle : MonoBehaviour
{
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 420, 80));
        bool on = GUILayout.Toggle(FSR4FrameGeneration.enabled, "Frame generation (experimental)");
        if (on != FSR4FrameGeneration.enabled)
            FSR4FrameGeneration.enabled = on;
        GUILayout.Label($"State: {FSR4FrameGeneration.state}");
        GUILayout.EndArea();
    }
}
#endif
