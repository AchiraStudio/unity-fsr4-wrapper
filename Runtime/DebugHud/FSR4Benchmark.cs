#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace AchiraStudio.FSR4.DebugHud
{
    /// <summary>What a finished benchmark run produced.</summary>
    public readonly struct FSR4BenchmarkResult
    {
        public readonly FSR4FrameSummary summary;
        public readonly float warmupSeconds, durationSeconds;
        /// <summary>Per-frame times (frame number, milliseconds). Empty when the files could not be written.</summary>
        public readonly string csvPath;
        /// <summary>Summary and the diagnostics report as JSON. Empty when the files could not be written.</summary>
        public readonly string jsonPath;

        internal FSR4BenchmarkResult(in FSR4FrameSummary summary, float warmup, float duration, string csv, string json)
        {
            this.summary = summary; warmupSeconds = warmup; durationSeconds = duration; csvPath = csv; jsonPath = json;
        }
    }

    /// <summary>
    /// Records frame times for a fixed time after a warm-up and writes them next to a diagnostics report, so two configurations
    /// (quality modes, upscaler on / off, frame generation on / off) can be compared with the same numbers.
    /// Start it from the debug HUD, from code, or with <c>-fsr4-benchmark</c> on the command line
    /// (<c>-fsr4-benchmark-warmup 5 -fsr4-benchmark-seconds 30 -fsr4-benchmark-quit</c>).
    /// <para>
    /// The times are Unity's frame times (what the game rendered). With frame generation on, the displayed frame rate is higher
    /// than these numbers; the report lists the frame-generation counters so the two can be related.
    /// </para>
    /// </summary>
    public static class FSR4Benchmark
    {
        [Serializable]
        sealed class JsonOut
        {
            public string startedUtc;
            public float warmupSeconds, durationSeconds;
            public int frames;
            public float averageMs, medianMs, worstMs, averageFps, low1PercentFps, low01PercentFps;
            public int framesOver33Ms;
            public string reportAtStart, reportAtEnd;
        }

        sealed class Driver : MonoBehaviour
        {
            public void Update() => Tick(Time.unscaledDeltaTime);
        }

        static Driver s_Driver;
        static float s_Warmup, s_Duration, s_Elapsed;
        static float[] s_Frames;
        static int s_Count;
        static string s_ReportAtStart;
        static DateTime s_Started;
        static bool s_QuitWhenDone;

        public static bool isRunning { get; private set; }

        /// <summary>0 to 1 over warm-up plus recording.</summary>
        public static float progress => isRunning && s_Warmup + s_Duration > 0f ? Mathf.Clamp01(s_Elapsed / (s_Warmup + s_Duration)) : 0f;

        /// <summary>Human-readable state or the outcome of the last run (including a file-write failure).</summary>
        public static string status { get; private set; } = "";

        public static event Action<FSR4BenchmarkResult> Completed;

        public static void Start(float warmupSeconds = 5f, float durationSeconds = 30f)
        {
            if (isRunning)
                return;
            s_Warmup = Mathf.Max(0f, warmupSeconds);
            s_Duration = Mathf.Max(1f, durationSeconds);
            s_Elapsed = 0f;
            s_Count = 0;
            // A generous capacity (up to 1000 fps) so that recording does not allocate while it measures.
            s_Frames = new float[(int)(s_Duration * 1000f) + 64];
            s_ReportAtStart = FSR4DiagnosticsReport.Build();
            s_Started = DateTime.UtcNow;
            isRunning = true;
            status = "Warming up";

            if (s_Driver == null)
            {
                var go = new GameObject("FSR4 Benchmark") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                s_Driver = go.AddComponent<Driver>();
            }
        }

        public static void Cancel()
        {
            if (!isRunning)
                return;
            Stop();
            status = "Cancelled";
        }

        static void Stop()
        {
            isRunning = false;
            if (s_Driver != null)
            {
                UnityEngine.Object.Destroy(s_Driver.gameObject);
                s_Driver = null;
            }
        }

        static void Tick(float dt)
        {
            if (!isRunning)
                return;

            s_Elapsed += dt;
            if (s_Elapsed <= s_Warmup)
                return;
            status = "Recording";

            if (s_Count < s_Frames.Length)
                s_Frames[s_Count++] = dt * 1000f;

            if (s_Elapsed >= s_Warmup + s_Duration || s_Count >= s_Frames.Length)
                Finish();
        }

        static void Finish()
        {
            Stop();
            var scratch = new float[s_Count];
            FSR4FrameSummary summary = FSR4FrameStatistics.Summarize(s_Frames, s_Count, scratch);
            string reportAtEnd = FSR4DiagnosticsReport.Build();

            string csv = "", json = "";
            try
            {
                string stamp = s_Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string dir = Application.persistentDataPath;
                string csvPath = Path.Combine(dir, "fsr4-benchmark-" + stamp + ".csv");
                string jsonPath = Path.Combine(dir, "fsr4-benchmark-" + stamp + ".json");

                var sb = new StringBuilder(s_Count * 12 + 256);
                sb.Append("frame,ms\n");
                for (int i = 0; i < s_Count; i++)
                    sb.Append(i).Append(',').Append(s_Frames[i].ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(csvPath, sb.ToString());

                var o = new JsonOut
                {
                    startedUtc = s_Started.ToString("o", CultureInfo.InvariantCulture),
                    warmupSeconds = s_Warmup, durationSeconds = s_Duration,
                    frames = summary.frames, averageMs = summary.averageMs, medianMs = summary.medianMs, worstMs = summary.worstMs,
                    averageFps = summary.averageFps, low1PercentFps = summary.low1PercentFps, low01PercentFps = summary.low01PercentFps,
                    framesOver33Ms = summary.framesOver33Ms,
                    reportAtStart = s_ReportAtStart, reportAtEnd = reportAtEnd,
                };
                File.WriteAllText(jsonPath, JsonUtility.ToJson(o, true));
                csv = csvPath; json = jsonPath;
                status = $"Done: {summary.averageFps:0.0} fps average, {summary.low1PercentFps:0.0} fps 1% low. Saved to {dir}";
            }
            catch (Exception e)
            {
                status = "Done, but the result files could not be written: " + e.Message;
            }

            var result = new FSR4BenchmarkResult(in summary, s_Warmup, s_Duration, csv, json);
            Debug.Log("[FSR4 Benchmark] " + status);
            try { Completed?.Invoke(result); }
            catch (Exception e) { Debug.LogException(e); }

            if (s_QuitWhenDone)
                Application.Quit();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Domain reload can be disabled in the Editor.
            isRunning = false; s_Driver = null; s_Frames = null; s_Count = 0; s_QuitWhenDone = false; status = "";
            Completed = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void StartFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool start = false;
            float warmup = 5f, seconds = 30f;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-fsr4-benchmark", StringComparison.OrdinalIgnoreCase)) start = true;
                else if (string.Equals(args[i], "-fsr4-benchmark-quit", StringComparison.OrdinalIgnoreCase)) s_QuitWhenDone = true;
                else if (string.Equals(args[i], "-fsr4-benchmark-warmup", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out warmup);
                else if (string.Equals(args[i], "-fsr4-benchmark-seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            }
            if (start)
                Start(warmup, seconds);
        }
    }
}
#endif
