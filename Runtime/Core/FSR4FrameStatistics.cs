#if ENABLE_UPSCALER_FRAMEWORK
using System;

namespace AchiraStudio.FSR4
{
    /// <summary>Summary of a set of frame times, in the form benchmark tools report them.</summary>
    public readonly struct FSR4FrameSummary
    {
        public readonly int frames;
        public readonly float averageMs, medianMs, worstMs;
        /// <summary>Frames per second from the average frame time.</summary>
        public readonly float averageFps;
        /// <summary>Frames per second of the slowest 1% of frames (average of their frame times). The usual "1% low".</summary>
        public readonly float low1PercentFps;
        /// <summary>Frames per second of the slowest 0.1% of frames. The usual "0.1% low".</summary>
        public readonly float low01PercentFps;
        /// <summary>Frames that took longer than 33.3 ms.</summary>
        public readonly int framesOver33Ms;

        internal FSR4FrameSummary(int frames, float averageMs, float medianMs, float worstMs, float low1, float low01, int over33)
        {
            this.frames = frames;
            this.averageMs = averageMs; this.medianMs = medianMs; this.worstMs = worstMs;
            averageFps = averageMs > 0f ? 1000f / averageMs : 0f;
            low1PercentFps = low1; low01PercentFps = low01;
            framesOver33Ms = over33;
        }
    }

    public static class FSR4FrameStatistics
    {
        /// <summary>
        /// Summarises <paramref name="count"/> frame times (milliseconds). <paramref name="scratch"/> must hold at least
        /// <paramref name="count"/> floats; it is sorted in place so that no memory is allocated per call.
        /// </summary>
        public static FSR4FrameSummary Summarize(float[] frameMs, int count, float[] scratch)
        {
            if (frameMs == null || scratch == null || count <= 0 || count > frameMs.Length || count > scratch.Length)
                return default;

            Array.Copy(frameMs, scratch, count);
            Array.Sort(scratch, 0, count);

            double sum = 0.0;
            int over33 = 0;
            for (int i = 0; i < count; i++)
            {
                sum += scratch[i];
                if (scratch[i] > 33.3f) over33++;
            }

            return new FSR4FrameSummary(count, (float)(sum / count), scratch[count / 2], scratch[count - 1],
                                        LowFps(scratch, count, 0.01), LowFps(scratch, count, 0.001), over33);
        }

        // Average of the slowest `fraction` of the frames (at least one frame), as frames per second.
        static float LowFps(float[] sorted, int count, double fraction)
        {
            int n = Math.Max(1, (int)Math.Ceiling(count * fraction - 1e-9));
            double sum = 0.0;
            for (int i = count - n; i < count; i++)
                sum += sorted[i];
            double avg = sum / n;
            return avg > 0.0 ? (float)(1000.0 / avg) : 0f;
        }
    }
}
#endif
