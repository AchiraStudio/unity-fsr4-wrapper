#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>
    /// The frame-generation block of the debug HUD. Registered with <see cref="FSR4HudSections"/>, so the HUD module shows it
    /// without either module referencing the other.
    /// </summary>
    internal sealed class FSR4FrameGenHudSection : IFSR4HudSection
    {
        public static readonly FSR4FrameGenHudSection instance = new FSR4FrameGenHudSection();

        readonly FSR4FrameRateMeter m_Meter = new FSR4FrameRateMeter();
        bool m_Probed;
        string m_Text = "";

        public string Title => "Frame generation";
        public int Order => 100;

        public void DrawGUI()
        {
            // IMGUI calls this once for layout and once for painting. The native state can change in between, so everything
            // that decides what is drawn is read during the layout event only.
            if (Event.current.type == EventType.Layout)
                Refresh();

            bool on = GUILayout.Toggle(FSR4FrameGeneration.enabled, " Enabled (experimental, Player builds only)");
            if (on != FSR4FrameGeneration.enabled)
                FSR4FrameGeneration.enabled = on;

            bool pacing = GUILayout.Toggle(FSR4FrameGeneration.pacing, " Pacing (hold the real frame for half an interval)");
            if (pacing != FSR4FrameGeneration.pacing)
                FSR4FrameGeneration.pacing = pacing;

            GUILayout.Label(m_Text);
        }

        void Refresh()
        {
            // Do not load the native plugin just to say that frame generation is off.
            m_Probed = FSR4FrameGeneration.enabled || FSR4FrameGeneration.initialised;
            if (!m_Probed)
            {
                m_Text = "State: Off";
                return;
            }

            m_Meter.Update();
            FSR4FrameGenState state = FSR4FrameGeneration.state;
            FSR4FrameGenStats s = FSR4FrameGeneration.GetStats();
            string provider = FSR4FrameGeneration.providerInUse;

            var sb = new System.Text.StringBuilder(384);
            sb.Append("State: ").Append(state).Append("   Backend: ").Append(FSR4FrameGeneration.backendName).Append('\n');
            sb.Append("Provider: ").Append(string.IsNullOrEmpty(provider) ? "(not reported)" : provider).Append('\n');
            if (state != FSR4FrameGenState.Active)
            {
                string message = FSR4FrameGeneration.statusMessage;
                if (!string.IsNullOrEmpty(message))
                    sb.Append(message).Append('\n');
            }
            sb.Append($"Rendered {m_Meter.renderedFps:0.0} fps + generated {m_Meter.generatedFps:0.0} fps = presented {m_Meter.presentedFps:0.0} fps\n");
            sb.Append($"Frames: {s.preparedFrames} prepared, {s.generatedFrames} generated, {s.passedThroughFrames} passed through\n");
            sb.Append($"Real frame interval {s.baseFrameMs:0.00} ms   Present hook {s.lastPresentHookMs:0.00} ms (CPU)\n");
            sb.Append($"GPU memory {s.vramBytes / (1024.0 * 1024.0):0.0} MB");
            if (s.lastFfxReturnCode != 0)
                sb.Append($"\nLast FidelityFX error code: {s.lastFfxReturnCode}");
            m_Text = sb.ToString();
        }
    }
}
#endif
