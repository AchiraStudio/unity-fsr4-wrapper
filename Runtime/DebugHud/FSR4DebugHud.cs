#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AchiraStudio.FSR4.DebugHud
{
    /// <summary>
    /// In-game performance monitor for the FSR package: upscaler status, rendered frame rate, CPU / GPU frame time, a frame-time
    /// graph, and one block per optional module (frame generation adds its own through <see cref="FSR4HudSections"/>).
    /// Add it to any GameObject, or start the Player with <c>-fsr4-hud</c>. Works in the Editor and in Player builds.
    /// </summary>
    [AddComponentMenu("AchiraStudio/FSR4 Debug HUD")]
    [DisallowMultipleComponent]
    public sealed class FSR4DebugHud : MonoBehaviour
    {
        [SerializeField, Tooltip("Show the window. The toggle key flips this at run time.")]
        bool m_Visible = true;

        [SerializeField, Tooltip("Key that shows / hides the window. None disables the shortcut.")]
        KeyCode m_ToggleKey = KeyCode.F10;

        [SerializeField, Tooltip("Draw the frame-time graph.")]
        bool m_ShowGraph = true;

        [SerializeField, Range(0.5f, 3f), Tooltip("Size of the window. Raise it on high-resolution displays.")]
        float m_Scale = 1f;

        public bool visible { get => m_Visible; set => m_Visible = value; }
        public KeyCode toggleKey { get => m_ToggleKey; set => m_ToggleKey = value; }
        public bool showGraph { get => m_ShowGraph; set => m_ShowGraph = value; }
        public float scale { get => m_Scale; set => m_Scale = Mathf.Clamp(value, 0.5f, 3f); }

        const int kWindowId = 0x46535234;   // "FSR4"
        const int kSamples = 180;
        const int kLongSamples = 1800;   // about 30 s at 60 fps, the window for the 1% / 0.1% lows
        const float kMeasureInterval = 0.5f;

        static int s_Instances;
        static readonly FrameTiming[] s_Timing = new FrameTiming[1];

        // Frame-time history (milliseconds), newest at m_Head - 1.
        readonly float[] m_FrameMs = new float[kSamples];
        int m_Head, m_Count;
        readonly float[] m_LongMs = new float[kLongSamples];
        readonly float[] m_Scratch = new float[kLongSamples];
        int m_LongHead, m_LongCount;
        FSR4FrameSummary m_Summary;
        string m_ToolStatus = "";
        readonly List<FSR4ViewStats> m_Views = new List<FSR4ViewStats>();

        int m_FpsFrames;
        float m_FpsTime;
        float m_Fps, m_AverageMs, m_WorstMs;
        double m_CpuMs, m_GpuMs;

        Rect m_Window = new Rect(12f, 12f, 400f, 0f);
        readonly List<IFSR4HudSection> m_Sections = new List<IFSR4HudSection>();
        string m_UpscalerText = "", m_PerformanceText = "";
        GUIStyle m_Header, m_Small;

        void OnEnable() => s_Instances++;
        void OnDisable() => s_Instances--;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_Instances = 0;   // domain reload can be disabled in the Editor

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void CreateFromCommandLine()
        {
            if (s_Instances > 0)
                return;
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (!string.Equals(arg, "-fsr4-hud", StringComparison.OrdinalIgnoreCase))
                    continue;
                var go = new GameObject("FSR4 Debug HUD");
                DontDestroyOnLoad(go);
                go.AddComponent<FSR4DebugHud>();
                return;
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            m_FrameMs[m_Head] = dt * 1000f;
            m_Head = (m_Head + 1) % kSamples;
            if (m_Count < kSamples)
                m_Count++;
            m_LongMs[m_LongHead] = dt * 1000f;
            m_LongHead = (m_LongHead + 1) % kLongSamples;
            if (m_LongCount < kLongSamples)
                m_LongCount++;

            m_FpsFrames++;
            m_FpsTime += dt;
            if (m_FpsTime >= kMeasureInterval)
            {
                m_Fps = m_FpsFrames / m_FpsTime;
                m_FpsFrames = 0;
                m_FpsTime = 0f;

                float sum = 0f, worst = 0f;
                for (int i = 0; i < m_Count; i++)
                {
                    sum += m_FrameMs[i];
                    if (m_FrameMs[i] > worst) worst = m_FrameMs[i];
                }
                m_AverageMs = m_Count > 0 ? sum / m_Count : 0f;
                m_WorstMs = worst;
                // The ring is not in time order, which does not matter for percentiles.
                m_Summary = FSR4FrameStatistics.Summarize(m_LongMs, m_LongCount, m_Scratch);
            }

            // Unity's own frame timings. They stay at zero when the platform or the build does not provide them.
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, s_Timing) > 0)
            {
                m_CpuMs = s_Timing[0].cpuFrameTime;
                m_GpuMs = s_Timing[0].gpuFrameTime;
            }
        }

        void OnGUI()
        {
            // IMGUI key events work with both the legacy Input Manager and the Input System package.
            Event e = Event.current;
            if (e.type == EventType.KeyDown && m_ToggleKey != KeyCode.None && e.keyCode == m_ToggleKey)
            {
                m_Visible = !m_Visible;
                e.Use();
            }
            if (!m_Visible)
                return;

            if (m_Header == null)
            {
                m_Header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                m_Small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(9, GUI.skin.label.fontSize - 2) };
            }

            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(m_Scale, m_Scale, 1f));
            m_Window = GUILayout.Window(kWindowId, m_Window, DrawWindow, "AchiraStudio FSR");
            GUI.matrix = oldMatrix;
        }

        void DrawWindow(int id)
        {
            // Everything that decides what gets drawn is sampled during the layout event, so the layout and the repaint event
            // of one frame always see the same controls.
            if (Event.current.type == EventType.Layout)
                Refresh();

            GUILayout.Label("Upscaler", m_Header);
            GUILayout.Label(m_UpscalerText);

            GUILayout.Label("Performance", m_Header);
            GUILayout.Label(m_PerformanceText);
            if (m_ShowGraph)
                DrawGraph();

            for (int i = 0; i < m_Sections.Count; i++)
            {
                GUILayout.Label(m_Sections[i].Title, m_Header);
                m_Sections[i].DrawGUI();
            }

            GUILayout.Label("Tools", m_Header);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy report"))
            {
                GUIUtility.systemCopyBuffer = FSR4DiagnosticsReport.Build();
                m_ToolStatus = "Report copied to the clipboard.";
            }
            if (GUILayout.Button("Save report"))
                m_ToolStatus = SaveReport();
            if (GUILayout.Button(FSR4Benchmark.isRunning ? "Cancel benchmark" : "Run benchmark"))
            {
                if (FSR4Benchmark.isRunning) FSR4Benchmark.Cancel(); else FSR4Benchmark.Start();
            }
            GUILayout.EndHorizontal();
            string toolText = FSR4Benchmark.isRunning ? $"Benchmark: {FSR4Benchmark.status} ({FSR4Benchmark.progress * 100f:0}%)" :
                              !string.IsNullOrEmpty(FSR4Benchmark.status) ? "Benchmark: " + FSR4Benchmark.status : m_ToolStatus;
            if (!string.IsNullOrEmpty(toolText))
                GUILayout.Label(toolText, m_Small);

            GUI.DragWindow();
        }

        static string SaveReport()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "fsr4-report-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt");
                System.IO.File.WriteAllText(path, FSR4DiagnosticsReport.Build());
                return "Report saved to " + path;
            }
            catch (Exception e)
            {
                return "Could not save the report: " + e.Message;
            }
        }

        // The text is rebuilt a few times per second, not on every IMGUI event: building strings every frame puts garbage on the
        // very heap this window is meant to help you watch. The layout and repaint events of one frame share the cached text.
        const float kRefreshInterval = 0.25f;
        float m_NextRefresh;

        void Refresh()
        {
            float now = Time.unscaledTime;
            if (now < m_NextRefresh)
                return;
            m_NextRefresh = now + kRefreshInterval;

            m_Sections.Clear();
            IReadOnlyList<IFSR4HudSection> all = FSR4HudSections.All;
            for (int i = 0; i < all.Count; i++)
                m_Sections.Add(all[i]);

            var sb = new StringBuilder(384);

            string status;
            if (!string.IsNullOrEmpty(FSR4Stats.notReadyReason))
                status = "Unavailable: " + FSR4Stats.notReadyReason;
            else if (FSR4Stats.lastRecordFrame < 0 || Time.frameCount - FSR4Stats.lastRecordFrame > 2)
                status = "Not running (select the upscaler in the URP asset)";
            else if (!string.IsNullOrEmpty(FSR4Stats.skipReason))
                status = "Skipped: " + FSR4Stats.skipReason;
            else
                status = "Active";
            sb.Append("Status: ").Append(status).Append('\n');

            string provider = FSR4Diagnostics.ProviderInUse();
            sb.Append("Backend: ").Append(FSR4Diagnostics.BackendName());
            sb.Append("   Provider: ").Append(string.IsNullOrEmpty(provider) ? "(not reported yet)" : provider).Append('\n');

            if (FSR4Stats.lastRecordFrame >= 0)
            {
                Vector2Int r = FSR4Stats.renderResolution, d = FSR4Stats.displayResolution;
                sb.Append($"Resolution: {r.x} x {r.y} -> {d.x} x {d.y}");
                if (FSR4Stats.optionsSeen)
                    sb.Append($"   Quality: {FSR4Stats.quality}");
                sb.Append('\n');
            }
            if (FSR4Stats.GetActiveViews(m_Views) > 1)
            {
                // Split-screen (or several upscaled cameras): every view has its own context and history.
                sb.Append($"Views: {m_Views.Count}\n");
                for (int i = 0; i < m_Views.Count; i++)
                {
                    FSR4ViewStats v = m_Views[i];
                    sb.Append($"  {v.cameraName}: {v.renderResolution.x} x {v.renderResolution.y} -> {v.displayResolution.x} x {v.displayResolution.y} (context {v.contextId})\n");
                }
            }
            if (FSR4Diagnostics.TryGetContextMemory(out ulong total, out _))
                sb.Append($"GPU memory: {total / (1024.0 * 1024.0):0.0} MB\n");
            if (FSR4Diagnostics.TryGetDispatchCounts(out ulong attempts, out ulong executed))
                sb.Append($"Dispatches: {executed} executed of {attempts}");
            m_UpscalerText = sb.ToString().TrimEnd('\n');

            sb.Clear();
            sb.Append($"Rendered: {m_Fps:0.0} fps\n");
            sb.Append($"Frame time: {m_AverageMs:0.00} ms average, {m_WorstMs:0.00} ms worst (last {m_Count} frames)\n");
            if (m_Summary.frames >= 100)
                sb.Append($"Lows: 1% {m_Summary.low1PercentFps:0.0} fps, 0.1% {m_Summary.low01PercentFps:0.0} fps (last {m_Summary.frames} frames)\n");
            if (m_CpuMs > 0.0 || m_GpuMs > 0.0)
                sb.Append($"CPU {m_CpuMs:0.00} ms   GPU {m_GpuMs:0.00} ms");
            else
                sb.Append("CPU / GPU frame time: not available (enable Frame Timing Stats in Player Settings)");
            m_PerformanceText = sb.ToString();
        }

        void DrawGraph()
        {
            Rect area = GUILayoutUtility.GetRect(10f, 56f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && m_Count > 0)
            {
                Color oldColor = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(area, Texture2D.whiteTexture);

                float top = 1000f / 30f;   // the graph shows at least 0 - 33.3 ms
                for (int i = 0; i < m_Count; i++)
                    if (m_FrameMs[i] > top) top = m_FrameMs[i];

                float barWidth = area.width / kSamples;
                for (int i = 0; i < m_Count; i++)
                {
                    float ms = m_FrameMs[(m_Head - m_Count + i + kSamples) % kSamples];
                    float height = Mathf.Max(1f, ms / top * area.height);
                    GUI.color = ms <= 16.7f ? new Color(0.35f, 0.85f, 0.4f) : ms <= 33.4f ? new Color(0.95f, 0.8f, 0.25f) : new Color(0.95f, 0.35f, 0.3f);
                    GUI.DrawTexture(new Rect(area.x + (kSamples - m_Count + i) * barWidth, area.yMax - height, Mathf.Max(1f, barWidth), height), Texture2D.whiteTexture);
                }
                GUI.color = oldColor;
                m_GraphTop = top;
            }
            GUILayout.Label($"Frame time per rendered frame, top of graph = {m_GraphTop:0.0} ms. Green up to 16.7 ms, yellow up to 33.4 ms.", m_Small);
        }

        float m_GraphTop = 1000f / 30f;
    }
}
#endif
