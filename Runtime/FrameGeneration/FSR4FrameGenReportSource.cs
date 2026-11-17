#if ENABLE_UPSCALER_FRAMEWORK
using System.Text;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>The frame-generation block of the diagnostics report (registered with <see cref="FSR4ReportSources"/>).</summary>
    internal sealed class FSR4FrameGenReportSource : IFSR4ReportSource
    {
        public static readonly FSR4FrameGenReportSource instance = new FSR4FrameGenReportSource();

        public string Title => "Frame generation";

        public void Append(StringBuilder sb)
        {
            // Do not load the native plugin just to say that frame generation is off.
            if (!FSR4FrameGeneration.enabled && !FSR4FrameGeneration.initialised)
            {
                sb.Append("Off (never enabled this session)\n");
                return;
            }

            FSR4FrameGenStats s = FSR4FrameGeneration.GetStats();
            string provider = FSR4FrameGeneration.providerInUse;
            sb.Append("Requested: ").Append(FSR4FrameGeneration.enabled ? "on" : "off").Append("   State: ").Append(FSR4FrameGeneration.state)
              .Append("   Backend: ").Append(FSR4FrameGeneration.backendName).Append('\n');
            sb.Append("Screen views: ").Append(FSR4FrameGeneration.screenViewCount).Append("   Pause in split-screen: ")
              .Append(FSR4FrameGeneration.pauseInSplitScreen ? "on" : "off")
              .Append(FSR4FrameGeneration.pausedForSplitScreen ? "   (paused right now)" : "").Append("   Frames skipped for split-screen: ")
              .Append(FSR4FrameGeneration.splitScreenPausedFrames).Append('\n');
            sb.Append("Provider: ").Append(string.IsNullOrEmpty(provider) ? "(not reported)" : provider).Append('\n');
            sb.Append("Pacing: ").Append(FSR4FrameGeneration.pacing ? "on" : "off").Append("   Debug overlays: ").Append(FSR4FrameGeneration.debug).Append('\n');
            sb.Append("Frames: ").Append(s.preparedFrames).Append(" prepared, ").Append(s.generatedFrames).Append(" generated, ")
              .Append(s.passedThroughFrames).Append(" passed through\n");
            sb.Append("Real frame interval: ").Append(s.baseFrameMs.ToString("0.00")).Append(" ms   Present hook: ")
              .Append(s.lastPresentHookMs.ToString("0.00")).Append(" ms (CPU)\n");
            sb.Append("GPU memory: ").Append((s.vramBytes / (1024.0 * 1024.0)).ToString("0.0")).Append(" MB\n");
            sb.Append("HUD-less input: ").Append(FSR4FrameGeneration.hudlessStatus).Append(" (").Append(FSR4FrameGeneration.hudlessFrames).Append(" frames)\n");
            if (s.lastFfxReturnCode != 0)
                sb.Append("Last FidelityFX error code: ").Append(s.lastFfxReturnCode).Append('\n');
            string message = FSR4FrameGeneration.statusMessage;
            if (!string.IsNullOrEmpty(message))
                sb.Append("Message: ").Append(message).Append('\n');
        }
    }
}
#endif
