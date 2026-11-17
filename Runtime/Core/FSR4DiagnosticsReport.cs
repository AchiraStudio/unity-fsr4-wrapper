#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4
{
    /// <summary>A block that an optional module adds to the diagnostics report (see <see cref="FSR4ReportSources"/>).</summary>
    public interface IFSR4ReportSource
    {
        string Title { get; }
        /// <summary>Append plain text lines. Called on the main thread when a report is built.</summary>
        void Append(StringBuilder sb);
    }

    public static class FSR4ReportSources
    {
        static readonly List<IFSR4ReportSource> s_Sources = new List<IFSR4ReportSource>();

        public static IReadOnlyList<IFSR4ReportSource> All => s_Sources;

        public static void Register(IFSR4ReportSource source)
        {
            if (source != null && !s_Sources.Contains(source))
                s_Sources.Add(source);
        }

        public static void Unregister(IFSR4ReportSource source) => s_Sources.Remove(source);
    }

    /// <summary>
    /// Builds a plain-text report of the setup (GPU, graphics API, upscaler status, optional modules) to paste into a bug report.
    /// It contains no user name, machine name or unique device id.
    /// </summary>
    public static class FSR4DiagnosticsReport
    {
        public static string Build()
        {
            var sb = new StringBuilder(2048);
            sb.Append("AchiraStudio FSR4 diagnostics report\n");
            sb.Append("Created (UTC): ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n\n");

            sb.Append("[System]\n");
            sb.Append("Unity: ").Append(Application.unityVersion).Append(Application.isEditor ? " (Editor)" : " (Player)").Append('\n');
            sb.Append("OS: ").Append(SystemInfo.operatingSystem).Append('\n');
            sb.Append("CPU: ").Append(SystemInfo.processorType).Append(", ").Append(SystemInfo.processorCount).Append(" threads, ")
              .Append(SystemInfo.systemMemorySize).Append(" MB RAM\n");
            sb.Append("GPU: ").Append(SystemInfo.graphicsDeviceName).Append(" (").Append(SystemInfo.graphicsDeviceVendor).Append(")\n");
            sb.Append("Graphics API: ").Append(SystemInfo.graphicsDeviceVersion).Append('\n');
            sb.Append("VRAM: ").Append(SystemInfo.graphicsMemorySize).Append(" MB\n");
            Resolution res = Screen.currentResolution;
            sb.Append("Display: ").Append(Screen.width).Append(" x ").Append(Screen.height).Append(" window, ")
              .Append(res.width).Append(" x ").Append(res.height).Append(" screen\n");
            sb.Append("VSync count: ").Append(QualitySettings.vSyncCount).Append("   Target frame rate: ").Append(Application.targetFrameRate).Append('\n');
            RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
            sb.Append("Render pipeline: ").Append(rp != null ? rp.name : "(built-in)").Append('\n');
            HDROutputSettings hdr = HDROutputSettings.main;
            sb.Append("HDR output: ").Append(hdr != null && hdr.available ? (hdr.active ? "active" : "available, off") : "not available").Append("\n\n");

            sb.Append("[Upscaler]\n");
            if (!string.IsNullOrEmpty(FSR4Stats.notReadyReason))
                sb.Append("Unavailable: ").Append(FSR4Stats.notReadyReason).Append('\n');
            else if (FSR4Stats.lastRecordFrame < 0 || Time.frameCount - FSR4Stats.lastRecordFrame > 2)
                sb.Append("Not running (select the upscaler in the URP asset)\n");
            else
            {
                sb.Append("Status: ").Append(string.IsNullOrEmpty(FSR4Stats.skipReason) ? "Active" : "Skipped: " + FSR4Stats.skipReason).Append('\n');
                Vector2Int r = FSR4Stats.renderResolution, d = FSR4Stats.displayResolution;
                sb.Append("Resolution: ").Append(r.x).Append(" x ").Append(r.y).Append(" -> ").Append(d.x).Append(" x ").Append(d.y).Append('\n');
                if (FSR4Stats.optionsSeen)
                    sb.Append("Quality: ").Append(FSR4Stats.quality).Append("   Provider setting: ").Append(FSR4Stats.provider)
                      .Append("   Resolution mode: ").Append(FSR4Stats.resolutionMode).Append('\n');
            }
            var views = new List<FSR4ViewStats>();
            if (FSR4Stats.GetActiveViews(views) > 0)
            {
                sb.Append("Views: ").Append(views.Count).Append('\n');
                for (int i = 0; i < views.Count; i++)
                {
                    FSR4ViewStats v = views[i];
                    sb.Append("  ").Append(v.cameraName).Append(" (context ").Append(v.contextId).Append("): ")
                      .Append(v.renderResolution.x).Append(" x ").Append(v.renderResolution.y).Append(" -> ")
                      .Append(v.displayResolution.x).Append(" x ").Append(v.displayResolution.y)
                      .Append(", ").Append(v.dispatches).Append(" dispatches\n");
                }
            }
            sb.Append("Backend: ").Append(FSR4Diagnostics.BackendName()).Append('\n');
            string provider = FSR4Diagnostics.ProviderInUse();
            sb.Append("Provider in use: ").Append(string.IsNullOrEmpty(provider) ? "(not reported)" : provider).Append('\n');
            if (FSR4Diagnostics.TryGetContextMemory(out ulong total, out ulong aliasable))
                sb.Append("Context memory: ").Append((total / (1024.0 * 1024.0)).ToString("0.0")).Append(" MB (")
                  .Append((aliasable / (1024.0 * 1024.0)).ToString("0.0")).Append(" MB aliasable)\n");
            if (FSR4Diagnostics.TryGetDispatchCounts(out ulong attempts, out ulong executed))
                sb.Append("Dispatches: ").Append(executed).Append(" executed of ").Append(attempts).Append('\n');

            IReadOnlyList<IFSR4ReportSource> sources = FSR4ReportSources.All;
            for (int i = 0; i < sources.Count; i++)
            {
                sb.Append("\n[").Append(sources[i].Title).Append("]\n");
                try { sources[i].Append(sb); }
                catch (Exception e) { sb.Append("(failed: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(")\n"); }
            }
            return sb.ToString();
        }
    }
}
#endif
