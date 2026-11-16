#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4
{
    /// <summary>
    /// What the upscaler pass actually saw on the most recent frame it ran. Written by FSR4Upscaler, read by the debug HUD.
    /// Plain statics (main thread only) - no allocations.
    /// </summary>
    public static class FSR4Stats
    {
        /// <summary>Set when the upscaler could not initialise (not D3D12, DLL missing, ...).</summary>
        public static string notReadyReason = "";

        /// <summary>Time.frameCount of the last frame the pass was recorded; -1 = never.</summary>
        public static int lastRecordFrame = -1;

        public static Vector2Int renderResolution;
        public static Vector2Int displayResolution;
        public static FSR4Quality quality;
        public static FSR4Provider provider;
        public static UpscalerResolutionMode resolutionMode;
        public static ulong versionIdRequested;
        public static bool optionsSeen;

        /// <summary>Why the last recorded pass did nothing (empty when it dispatched).</summary>
        public static string skipReason = "";

        /// <summary>Dispatch events issued from the main thread since startup.</summary>
        public static long dispatchesIssued;

        internal static void MarkRecorded(Vector2Int render, Vector2Int display, FSR4Options o, ulong versionId)
        {
            lastRecordFrame = Time.frameCount;
            renderResolution = render;
            displayResolution = display;
            versionIdRequested = versionId;
            optionsSeen = o != null;
            if (o != null)
            {
                quality = o.qualityMode;
                provider = o.provider;
                resolutionMode = o.resolutionMode;
            }
            skipReason = "";
        }

        internal static void MarkSkipped(string reason)
        {
            lastRecordFrame = Time.frameCount;
            skipReason = reason;
        }
    }
}
#endif
