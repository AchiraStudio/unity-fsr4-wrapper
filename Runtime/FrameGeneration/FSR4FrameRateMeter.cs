#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>
    /// Measures the rendered and the generated frame rate separately. Create one, call <see cref="Update"/> from anywhere
    /// (once per frame or more often, it does not matter) and read the properties.
    /// <para>
    /// Both rates are measured, not derived from each other: rendered frames come from Unity's frame counter, generated frames
    /// from the native plugin's counter of interpolated frames it handed to the swapchain. A generated frame is counted when its
    /// present call succeeded, which does not prove that the display showed it.
    /// </para>
    /// </summary>
    public sealed class FSR4FrameRateMeter
    {
        /// <summary>Seconds between two measurements.</summary>
        public float interval = 0.5f;

        /// <summary>Frames per second the game rendered.</summary>
        public float renderedFps { get; private set; }

        /// <summary>Frames per second added by frame generation (0 while it is off).</summary>
        public float generatedFps { get; private set; }

        /// <summary>Frames per second handed to the swapchain: rendered plus generated.</summary>
        public float presentedFps => renderedFps + generatedFps;

        int m_LastFrame = -1;
        float m_LastTime;
        ulong m_LastGenerated;

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            int frame = Time.frameCount;
            if (m_LastFrame < 0 || frame < m_LastFrame)
            {
                m_LastFrame = frame;
                m_LastTime = now;
                m_LastGenerated = ReadGenerated();
                return;
            }

            float elapsed = now - m_LastTime;
            if (elapsed < interval || elapsed <= 0f)
                return;

            ulong generated = ReadGenerated();
            renderedFps = (frame - m_LastFrame) / elapsed;
            generatedFps = generated > m_LastGenerated ? (generated - m_LastGenerated) / elapsed : 0f;

            m_LastFrame = frame;
            m_LastTime = now;
            m_LastGenerated = generated;
        }

        // The native counter only moves while frame generation runs, so it is not read (and the plugin not loaded) while it is off.
        ulong ReadGenerated()
            => FSR4FrameGeneration.enabled ? FSR4FrameGeneration.GetStats().generatedFrames : m_LastGenerated;
    }
}
#endif
