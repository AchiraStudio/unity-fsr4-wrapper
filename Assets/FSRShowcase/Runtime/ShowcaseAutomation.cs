using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AchiraStudio.FSR4;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FSRShowcase
{
    /// <summary>
    /// Scripted test run for a Player build. Launch with:
    ///   FSRShowcase.exe -fsr-run smoke,matrix,motion,splitscreen,framegen,hud,menu -fsr-out &lt;folder&gt;
    /// Every step writes screenshots, and results.json records what was requested, what the wrapper reported, and measured frame rates.
    /// Screenshots are taken with ScreenCapture, which reads the backbuffer Unity rendered. Generated frames only exist in the swapchain,
    /// so frame-generation steps also ask an external watcher script for real desktop captures (see ExternalCapture).
    /// </summary>
    public sealed class ShowcaseAutomation : MonoBehaviour
    {
        [Serializable]
        public class Entry
        {
            public string id, group, label, file;
            public string requestedProvider, providerInUse, quality, upscaler;
            public string renderRes, displayRes;
            public float avgFps, avgFrameMs;
            public float renderedFps, generatedFps, presentedFps;
            public string fgState, fgMessage, fgProvider, hudlessStatus;
            public int views, fgScreenViews;
            public bool fgPausedForSplit;
            public string notes;
        }

        [Serializable]
        public class Report
        {
            public string startedUtc, unity, gpu, graphicsApi, os, cpu, vramMb;
            public int displayWidth, displayHeight;
            public string scene = "Oasis (copy)";
            public List<Entry> entries = new List<Entry>();
        }

        string m_Out, m_Ext;
        readonly Report m_Report = new Report();
        StreamWriter m_Log;
        ShowcaseController m_C;
        ShowcaseSettings m_Base;

        public void Begin(string modes, string outDir)
        {
            m_C = ShowcaseController.Instance;
            m_Out = string.IsNullOrEmpty(outDir) ? Path.Combine(Application.persistentDataPath, "FSRShowcase") : outDir;
            m_Ext = Path.Combine(m_Out, "ext");
            Directory.CreateDirectory(m_Out);
            Directory.CreateDirectory(m_Ext);
            m_Log = new StreamWriter(Path.Combine(m_Out, "automation.log"), false) { AutoFlush = true };
            StartCoroutine(Run(modes.Split(',')));
        }

        void Log(string s)
        {
            string line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {s}";
            Debug.Log("[Showcase] " + s);
            m_Log?.WriteLine(line);
        }

        // ------------------------------------------------------------------------------------------------------------------
        // top level
        // ------------------------------------------------------------------------------------------------------------------
        IEnumerator Run(string[] modes)
        {
            m_Report.startedUtc = DateTime.UtcNow.ToString("o");
            m_Report.unity = Application.unityVersion;
            m_Report.gpu = SystemInfo.graphicsDeviceName + " (driver " + SystemInfo.graphicsDeviceVersion + ")";
            m_Report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            m_Report.os = SystemInfo.operatingSystem;
            m_Report.cpu = SystemInfo.processorType;
            m_Report.vramMb = SystemInfo.graphicsMemorySize.ToString();

            m_C.TakeCameraControl();
            // -fsr-res 3840x2160 runs the whole script at another output resolution (default 1920x1080).
            int resW = 1920, resH = 1080;
            var cmd = Environment.GetCommandLineArgs();
            for (int i = 0; i < cmd.Length - 1; i++)
            {
                if (cmd[i] != "-fsr-res") continue;
                var parts = cmd[i + 1].ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh)) { resW = rw; resH = rh; }
            }
            m_Base = new ShowcaseSettings
            {
                // borderless when the output fits the desktop, a normal window when it is larger (then Windows lets it overhang)
                width = resW, height = resH, displayMode = resW > Display.main.systemWidth ? 2 : 0, vsync = 0, frameCap = 0, fov = 55f,
                upscalingEnabled = true, legacyUpscalers = false, fsrGeneration = FsrGeneration.FSR3, quality = 1,
                frameGen = false, showPerfOverlay = true, showDebugHud = false, hudTest = HudTestKind.Off,
            };
            m_Base.ApplyPreset(3);
            m_Base.preset = 3;
            m_C.ApplyFromCode(m_Base, true);
            m_C.PoseCamera(0);
            yield return WaitSeconds(4f);
            m_Report.displayWidth = Screen.width;
            m_Report.displayHeight = Screen.height;
            Log($"Start: {m_Report.unity}  {m_Report.gpu}  {m_Report.graphicsApi}  {Screen.width}x{Screen.height}");
            Log($"Pipeline upscaler: {GraphicsController.ActiveUpscalerName}  wrapper active: {GraphicsController.WrapperActive}");

            foreach (string raw in modes)
            {
                string mode = raw.Trim().ToLowerInvariant();
                Log("=== mode: " + mode);
                IEnumerator step = null;
                switch (mode)
                {
                    case "smoke": step = Smoke(); break;
                    case "matrix": step = Matrix(); break;
                    case "motion": step = Motion(); break;
                    case "splitscreen": step = SplitScreen(); break;
                    case "framegen": step = FrameGen(); break;
                    case "hud": step = Hud(); break;
                    case "menu": step = MenuTour(); break;
                    case "perf": step = Perf(); break;
                }
                if (step != null)
                {
                    yield return step;
                    Flush();
                }
            }

            Flush();
            File.WriteAllText(Path.Combine(m_Out, "done.flag"), DateTime.UtcNow.ToString("o"));
            Log("All steps finished.");
            yield return WaitSeconds(1f);
            Application.Quit();
        }

        void Flush() => File.WriteAllText(Path.Combine(m_Out, "results.json"), JsonUtility.ToJson(m_Report, true));

        // ------------------------------------------------------------------------------------------------------------------
        // steps
        // ------------------------------------------------------------------------------------------------------------------
        IEnumerator Smoke()
        {
            var s = m_Base.Clone();
            s.fsrGeneration = FsrGeneration.Auto; s.quality = 1;
            m_C.ApplyFromCode(s);
            yield return Settle(2.5f, 90);
            yield return Capture("smoke_fsr_auto_quality", "smoke", "Auto / Quality", s);

            s.frameGen = true;
            m_C.ApplyFromCode(s);
            yield return Settle(4f, 120);
            yield return Capture("smoke_framegen", "smoke", "Frame generation requested", s);
        }

        IEnumerator Matrix()
        {
            string[] qn = { "Native AA", "Quality", "Balanced", "Performance", "Ultra Performance" };
            for (int pp = 0; pp < 2; pp++)
            {
                m_C.PoseCamera(pp);
                string pname = pp == 0 ? "vista" : "foliage";

                // baseline: native resolution, engine TAA
                var n = m_Base.Clone();
                n.upscalingEnabled = false;
                m_C.ApplyFromCode(n);
                yield return Settle(2.5f, 90);
                yield return Capture($"matrix_{pname}_native_taa", $"matrix-{pname}", "Native (TAA)", n);

                foreach (FsrGeneration gen in new[] { FsrGeneration.FSR4, FsrGeneration.FSR3, FsrGeneration.FSR2 })
                {
                    // ids use the FSR major version (fsr4 / fsr3 / fsr2), not the enum value
                    int major = gen == FsrGeneration.FSR4 ? 4 : gen == FsrGeneration.FSR3 ? 3 : 2;
                    for (int q = 0; q < 5; q++)
                    {
                        var s = m_Base.Clone();
                        s.fsrGeneration = gen; s.quality = q;
                        m_C.ApplyFromCode(s);
                        yield return Settle(2.5f, 100);
                        yield return Capture($"matrix_{pname}_fsr{major}_{q}", $"matrix-{pname}", $"FSR {major} / {qn[q]}", s, measureSeconds: 3f);
                    }
                }

                // legacy upscalers at comparable render scales
                foreach (var (kind, scale) in new[] { (LegacyUpscaler.STP, 0.67f), (LegacyUpscaler.STP, 0.5f), (LegacyUpscaler.FSR1Spatial, 0.67f), (LegacyUpscaler.FSR1Spatial, 0.5f), (LegacyUpscaler.Bilinear, 0.5f) })
                {
                    var s = m_Base.Clone();
                    s.legacyUpscalers = true; s.legacyKind = kind; s.renderScale = scale;
                    m_C.ApplyFromCode(s);
                    yield return Settle(2.5f, 90);
                    yield return Capture($"matrix_{pname}_legacy_{kind}_{Mathf.RoundToInt(scale * 100)}", $"matrix-{pname}", $"Legacy {kind} @ {scale:P0}", s);
                }
            }
        }

        /// <summary>
        /// Performance under a GPU-bound frame. At 1080p this scene is CPU-limited (see the matrix), so the quality modes cannot show their
        /// gain there. This step adds the synthetic render-resolution load (-fsr-load 0..1, default 0.4) and measures every mode, frame
        /// generation with and without pacing, and split-screen.
        /// </summary>
        IEnumerator Perf()
        {
            float load = 0.4f;
            var cmd = Environment.GetCommandLineArgs();
            for (int i = 0; i < cmd.Length - 1; i++)
                if (cmd[i] == "-fsr-load" && float.TryParse(cmd[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float l)) load = l;
            Log($"perf: synthetic GPU load {load:0.00} ({Mathf.RoundToInt(load * GraphicsController.GpuLoadMaxIterations)} iterations per pixel)");

            string[] qn = { "Native AA", "Quality", "Balanced", "Performance", "Ultra Performance" };
            m_C.PoseCamera(0);

            var n = m_Base.Clone(); n.upscalingEnabled = false; n.gpuLoad = load;
            m_C.ApplyFromCode(n);
            yield return Settle(3f, 100);
            yield return Capture("perf_native_taa", "perf", "Native (TAA)", n, measureSeconds: 4f);

            foreach (FsrGeneration gen in new[] { FsrGeneration.FSR4, FsrGeneration.FSR3, FsrGeneration.FSR2 })
            {
                int major = gen == FsrGeneration.FSR4 ? 4 : gen == FsrGeneration.FSR3 ? 3 : 2;
                for (int q = 0; q < 5; q++)
                {
                    var s = m_Base.Clone(); s.fsrGeneration = gen; s.quality = q; s.gpuLoad = load;
                    m_C.ApplyFromCode(s);
                    yield return Settle(2.5f, 100);
                    yield return Capture($"perf_fsr{major}_{q}", "perf", $"FSR {major} / {qn[q]}", s, measureSeconds: 4f);
                }
            }
            foreach (var (kind, scale) in new[] { (LegacyUpscaler.STP, 0.67f), (LegacyUpscaler.STP, 0.5f), (LegacyUpscaler.FSR1Spatial, 0.5f) })
            {
                var s = m_Base.Clone(); s.legacyUpscalers = true; s.legacyKind = kind; s.renderScale = scale; s.gpuLoad = load;
                m_C.ApplyFromCode(s);
                yield return Settle(2.5f, 100);
                yield return Capture($"perf_legacy_{kind}_{Mathf.RoundToInt(scale * 100)}", "perf", $"Legacy {kind} @ {scale:P0}", s, measureSeconds: 4f);
            }

            // frame generation: off, on with pacing, on without pacing (FSR 4, Quality)
            var f = m_Base.Clone(); f.fsrGeneration = FsrGeneration.FSR4; f.quality = 1; f.gpuLoad = load; f.showPerfOverlay = true;
            m_C.ApplyFromCode(f);
            yield return Settle(3f, 100);
            yield return Capture("perf_fg_off", "perf-fg", "Frame generation off", f, measureSeconds: 5f);
            f.frameGen = true; f.pacing = true;
            m_C.ApplyFromCode(f);
            yield return Settle(5f, 150);
            yield return Capture("perf_fg_on", "perf-fg", "Frame generation on (pacing on)", f, measureSeconds: 6f);
            f.pacing = false;
            m_C.ApplyFromCode(f);
            yield return Settle(4f, 120);
            yield return Capture("perf_fg_on_nopacing", "perf-fg", "Frame generation on (pacing off)", f, measureSeconds: 6f);
            f.frameGen = false; f.pacing = true;
            m_C.ApplyFromCode(f);
            yield return Settle(3f, 90);

            // split-screen: native vs FSR 4 modes, no frame generation
            m_C.SetSplitScreen(true);
            var sn = m_Base.Clone(); sn.upscalingEnabled = false; sn.gpuLoad = load;
            m_C.ApplyFromCode(sn);
            yield return Settle(3f, 100);
            yield return Capture("perf_split_native_taa", "perf-split", "Split-screen / native (TAA)", sn, measureSeconds: 4f);
            foreach (int q in new[] { 1, 3, 4 })
            {
                var s = m_Base.Clone(); s.fsrGeneration = FsrGeneration.FSR4; s.quality = q; s.gpuLoad = load;
                m_C.ApplyFromCode(s);
                yield return Settle(3f, 100);
                yield return Capture($"perf_split_fsr4_{q}", "perf-split", $"Split-screen / FSR 4 {qn[q]}", s, measureSeconds: 4f);
            }
            m_C.SetSplitScreen(false);
            var back = m_Base.Clone();
            m_C.ApplyFromCode(back);
            yield return Settle(1f, 30);
        }

        IEnumerator Motion()
        {
            m_C.PoseCamera(1);
            var cam = Camera.main;
            Vector3 p0 = cam.transform.position;
            Quaternion r0 = cam.transform.rotation;

            var configs = new List<(string id, string label, ShowcaseSettings s)>();
            var n = m_Base.Clone(); n.upscalingEnabled = false; configs.Add(("native_taa", "Native (TAA)", n));
            foreach (int q in new[] { 1, 3, 4 })
            {
                var s = m_Base.Clone(); s.fsrGeneration = FsrGeneration.FSR3; s.quality = q; configs.Add(($"fsr3_{q}", $"FSR 3 / q{q}", s));
            }
            var s2 = m_Base.Clone(); s2.fsrGeneration = FsrGeneration.FSR2; s2.quality = 3; configs.Add(("fsr2_3", "FSR 2 / Performance", s2));

            foreach (var c in configs)
            {
                m_C.ApplyFromCode(c.s);
                cam.transform.SetPositionAndRotation(p0, r0);
                yield return Settle(2f, 60);
                // pan the camera sideways at 3 m/s for ~1.5 s, capture while still moving
                float t = 0f;
                while (true)
                {
                    t += Time.unscaledDeltaTime;
                    cam.transform.position = p0 + r0 * Vector3.right * (t * 3f);
                    cam.transform.rotation = r0 * Quaternion.Euler(0f, t * 12f, 0f);
                    if (t >= 1.5f) break;     // capture in the very frame the camera moved, so motion vectors are non-zero
                    yield return null;
                }
                var e = Describe("motion_" + c.id, "motion", c.label + " (camera panning)", c.s);
                yield return Shot(e.id);
                e.file = e.id + ".png";
                m_Report.entries.Add(e);
                Log($"{e.id}: {e.label} | {e.providerInUse} | {e.renderRes} -> {e.displayRes}");
            }
            cam.transform.SetPositionAndRotation(p0, r0);
        }

        IEnumerator SplitScreen()
        {
            var s = m_Base.Clone(); s.fsrGeneration = FsrGeneration.FSR3; s.quality = 1;
            m_C.PoseCamera(0);
            m_C.ApplyFromCode(s);
            yield return Settle(2.5f, 90);

            m_C.SetSplitScreen(true);

            // control: two views, no upscaling (engine TAA at full resolution)
            var nat = m_Base.Clone(); nat.upscalingEnabled = false;
            m_C.ApplyFromCode(nat);
            yield return Settle(3f, 120);
            yield return Capture("split_native_taa", "splitscreen", "Split-screen / native (TAA)", nat, measureSeconds: 3f);

            foreach (int q in new[] { 0, 1, 2, 3, 4 })
            {
                s.quality = q;
                m_C.ApplyFromCode(s);
                yield return Settle(3f, 120);
                float msSum = 0f; int msN = 0;
                for (float t = 0f; t < 3f; ) { float dt = Time.unscaledDeltaTime; t += dt; msSum += dt; msN++; yield return null; }
                var e = Describe($"split_fsr3_q{q}", "splitscreen", $"Split-screen / FSR 3 q{q}", s);
                e.avgFrameMs = msN > 0 ? msSum / msN * 1000f : 0f;
                e.avgFps = e.avgFrameMs > 0f ? 1000f / e.avgFrameMs : 0f;
                yield return Shot(e.id);
                e.file = e.id + ".png";
                var views = new List<FSR4ViewStats>();
                FSR4Stats.GetActiveViews(views);
                var sb = new System.Text.StringBuilder();
                foreach (var v in views) sb.Append($"[{v.cameraName}: ctx {v.contextId}, {v.renderResolution.x}x{v.renderResolution.y} -> {v.displayResolution.x}x{v.displayResolution.y}, {v.dispatches} dispatches] ");
                e.notes = sb.ToString();
                e.views = views.Count;
                m_Report.entries.Add(e);
                Log($"split q{q}: views={views.Count} {e.notes}");
            }

            // legacy upscaler in split screen as a control
            var l = m_Base.Clone(); l.legacyUpscalers = true; l.legacyKind = LegacyUpscaler.STP; l.renderScale = 0.67f;
            m_C.ApplyFromCode(l);
            yield return Settle(2.5f, 90);
            yield return Capture("split_stp_067", "splitscreen", "Split-screen / STP 67%", l, measureSeconds: 3f);

            // frame generation requested while split: expected to pause
            s.quality = 1; s.frameGen = true;
            m_C.ApplyFromCode(s);
            yield return Settle(4f, 120);
            yield return Capture("split_framegen_requested", "splitscreen", "Split-screen / frame generation requested", s);

            m_C.SetSplitScreen(false);
            s.frameGen = false;
            m_C.ApplyFromCode(s);
            yield return Settle(1f, 30);
        }

        IEnumerator FrameGen()
        {
            m_C.PoseCamera(0);
            var s = m_Base.Clone(); s.fsrGeneration = FsrGeneration.FSR3; s.quality = 1; s.showPerfOverlay = true; s.showDebugHud = true;
            m_C.ApplyFromCode(s);
            yield return Settle(3f, 120);
            yield return Capture("fg_off", "framegen", "Frame generation off", s, measureSeconds: 6f);

            s.frameGen = true;
            m_C.ApplyFromCode(s);
            yield return Settle(5f, 150);
            yield return Capture("fg_on", "framegen", "Frame generation on", s, measureSeconds: 8f);
            yield return ExternalCapture("fg_on_desktop", 6, 0.25f);

            // Pacing waits half a frame interval on the render thread so generated and real frames are evenly spaced. When the
            // render thread is the bottleneck that wait is not hidden, so compare with pacing off.
            s.pacing = false;
            m_C.ApplyFromCode(s);
            yield return Settle(4f, 120);
            yield return Capture("fg_on_nopacing", "framegen", "Frame generation on, pacing off", s, measureSeconds: 6f);
            s.pacing = true;

            s.hudless = true;
            m_C.ApplyFromCode(s);
            yield return Settle(4f, 120);
            yield return Capture("fg_on_hudless", "framegen", "Frame generation on + HUD-less", s, measureSeconds: 6f);

            s.frameGen = false; s.hudless = false; s.showDebugHud = false;
            m_C.ApplyFromCode(s);
            yield return Settle(3f, 90);
            yield return Capture("fg_off_again", "framegen", "Frame generation off again", s, measureSeconds: 4f);
        }

        IEnumerator Hud()
        {
            m_C.PoseCamera(1);
            foreach (HudTestKind kind in new[] { HudTestKind.OverlayUIToolkit, HudTestKind.ScreenSpaceCamera, HudTestKind.WorldSpace })
            {
                foreach (var (fg, hudless, tag) in new[] { (false, false, "fsr"), (true, false, "fsr_fg"), (true, true, "fsr_fg_hudless") })
                {
                    var s = m_Base.Clone();
                    s.fsrGeneration = FsrGeneration.FSR3; s.quality = 3;
                    s.hudTest = kind; s.frameGen = fg; s.hudless = hudless; s.showPerfOverlay = true;
                    m_C.ApplyFromCode(s);
                    yield return Settle(4f, 120);
                    string id = $"hud_{kind}_{tag}";
                    yield return Capture(id, "hud", $"{kind} / {tag}", s, measureSeconds: 3f);
                    if (fg)
                        yield return ExternalCapture(id + "_desktop", 8, 0.12f);
                }
            }
            // native, non-upscaled reference
            var n = m_Base.Clone(); n.upscalingEnabled = false; n.hudTest = HudTestKind.ScreenSpaceCamera;
            m_C.ApplyFromCode(n);
            yield return Settle(3f, 90);
            yield return Capture("hud_ScreenSpaceCamera_native", "hud", "ScreenSpaceCamera / native", n);
            n.hudTest = HudTestKind.Off;
            m_C.ApplyFromCode(n);
        }

        IEnumerator MenuTour()
        {
            var s = m_Base.Clone(); s.fsrGeneration = FsrGeneration.FSR3; s.quality = 1; s.frameGen = true;
            m_C.PoseCamera(0);
            m_C.ApplyFromCode(s);
            yield return Settle(3f, 90);
            m_C.SetMenuOpen(true);
            string[] tabs = { "display", "upscaling", "framegen", "advanced", "overlays" };
            for (int i = 0; i < tabs.Length; i++)
            {
                m_C.Menu.SelectTab(i);
                yield return Settle(1.2f, 20);
                yield return Capture("menu_" + tabs[i], "menu", "Menu / " + tabs[i], s, skipMeasure: true);
            }
            m_C.SetMenuOpen(false);
            s.frameGen = false;
            m_C.ApplyFromCode(s);
        }

        // ------------------------------------------------------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------------------------------------------------------
        /// <summary>Waits at least <paramref name="seconds"/> and at least <paramref name="frames"/> frames: slow GPUs need frames, fast ones need time.</summary>
        IEnumerator Settle(float seconds, int frames)
        {
            float t = 0f; int f = 0;
            while (t < seconds || f < frames)
            {
                t += Time.unscaledDeltaTime; f++;
                yield return null;
                if (t > 40f) break;
            }
        }

        static IEnumerator WaitSeconds(float s)
        {
            float t = 0f;
            while (t < s) { t += Time.unscaledDeltaTime; yield return null; }
        }

        Entry Describe(string id, string group, string label, ShowcaseSettings s)
        {
            var e = new Entry { id = id, group = group, label = label };
            e.requestedProvider = s.upscalingEnabled ? (s.legacyUpscalers ? "legacy:" + s.legacyKind : s.fsrGeneration.ToString()) : "off";
            e.quality = s.upscalingEnabled && !s.legacyUpscalers ? ((FSR4Quality)s.quality).ToString() : (s.legacyUpscalers ? $"scale {s.renderScale:0.00}" : "native");
            e.upscaler = GraphicsController.ActiveUpscalerName;
            e.providerInUse = GraphicsController.WrapperActive ? FSR4Diagnostics.ProviderInUse() : "";
            if (GraphicsController.WrapperActive && FSR4Stats.renderResolution.x > 0)
            {
                e.renderRes = $"{FSR4Stats.renderResolution.x}x{FSR4Stats.renderResolution.y}";
                e.displayRes = $"{FSR4Stats.displayResolution.x}x{FSR4Stats.displayResolution.y}";
            }
            else
            {
                float sc = GraphicsController.Asset != null ? GraphicsController.Asset.renderScale : 1f;
                e.renderRes = $"{Mathf.RoundToInt(Screen.width * sc)}x{Mathf.RoundToInt(Screen.height * sc)}";
                e.displayRes = $"{Screen.width}x{Screen.height}";
            }
            var m = m_C.Perf.meter;
            e.renderedFps = m.renderedFps; e.generatedFps = m.generatedFps; e.presentedFps = m.presentedFps;
            e.fgState = FSR4FrameGeneration.state.ToString();
            e.fgMessage = FSR4FrameGeneration.statusMessage;
            e.fgProvider = FSR4FrameGeneration.providerInUse;
            e.hudlessStatus = FSR4FrameGeneration.hudlessStatus;
            e.fgScreenViews = FSR4FrameGeneration.screenViewCount;
            e.fgPausedForSplit = FSR4FrameGeneration.pausedForSplitScreen;
            return e;
        }

        IEnumerator Capture(string id, string group, string label, ShowcaseSettings s, bool skipMeasure = false, bool keepMoving = false, float measureSeconds = 1.5f)
        {
            float avgMs = 0f;
            if (!skipMeasure)
            {
                float t = 0f; int n = 0; double sum = 0;
                while (t < measureSeconds) { float dt = Time.unscaledDeltaTime; t += dt; sum += dt; n++; yield return null; }
                avgMs = n > 0 ? (float)(sum / n * 1000.0) : 0f;
            }
            var e = Describe(id, group, label, s);
            e.avgFrameMs = avgMs;
            e.avgFps = avgMs > 0f ? 1000f / avgMs : 0f;
            yield return Shot(id);
            e.file = id + ".png";
            m_Report.entries.Add(e);
            Log($"{id}: {e.label} | req {e.requestedProvider} -> in use '{e.providerInUse}' | {e.renderRes} -> {e.displayRes} | {e.avgFps:0.0} fps | FG {e.fgState} r{e.renderedFps:0.0}+g{e.generatedFps:0.0}");
        }

        IEnumerator Shot(string id)
        {
            string path = Path.Combine(m_Out, id + ".png");
            if (File.Exists(path)) File.Delete(path);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            float t = 0f;
            while (!File.Exists(path) && t < 6f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return null;
            yield return null;
        }

        /// <summary>
        /// Asks the external watcher (tools/desktop-capture.ps1) for desktop screenshots, which include frames added by frame generation.
        /// The step simply waits; if no watcher is running it times out and the run continues.
        /// </summary>
        IEnumerator ExternalCapture(string name, int count, float interval)
        {
            string req = Path.Combine(m_Ext, name + ".req");
            string done = Path.Combine(m_Ext, name + ".done");
            if (File.Exists(done)) File.Delete(done);
            File.WriteAllText(req, count + "," + interval.ToString(System.Globalization.CultureInfo.InvariantCulture));
            float t = 0f;
            while (!File.Exists(done) && t < 25f) { t += Time.unscaledDeltaTime; yield return null; }
            Log($"external capture '{name}': {(File.Exists(done) ? "done" : "timed out (no watcher?)")}");
        }
    }
}
