#if ENABLE_UPSCALER_FRAMEWORK
using UnityEngine;
using UnityEngine.Rendering;

using System.Collections.Generic;

namespace AchiraStudio.FSR4
{
    /// <summary>What one upscaled view (camera) did recently. Split-screen has one per player.</summary>
    public readonly struct FSR4ViewStats
    {
        public readonly string cameraName;
        /// <summary>Native context id: every view has its own context and therefore its own temporal history.</summary>
        public readonly int contextId;
        public readonly Vector2Int renderResolution, displayResolution;
        /// <summary>Upscale dispatches issued for this view since its context was first seen.</summary>
        public readonly long dispatches;

        internal FSR4ViewStats(string cameraName, int contextId, Vector2Int render, Vector2Int display, long dispatches)
        {
            this.cameraName = cameraName; this.contextId = contextId;
            renderResolution = render; displayResolution = display; this.dispatches = dispatches;
        }
    }

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

        // One slot per context id (a context is one camera's temporal history). Fixed size, no allocation per frame: the
        // camera name is read once, when the slot is created.
        const int kMaxViews = 8;
        sealed class ViewSlot
        {
            public int contextId, lastFrame = -1;
            public string cameraName = "";
            public Vector2Int render, display;
            public long dispatches;
        }
        static readonly ViewSlot[] s_Views = CreateViewSlots();

        static ViewSlot[] CreateViewSlots()
        {
            var a = new ViewSlot[kMaxViews];
            for (int i = 0; i < a.Length; i++) a[i] = new ViewSlot();
            return a;
        }

        static ViewSlot FindView(int contextId, Camera camera, bool create)
        {
            ViewSlot free = null, oldest = s_Views[0];
            for (int i = 0; i < s_Views.Length; i++)
            {
                ViewSlot v = s_Views[i];
                if (v.contextId == contextId) return v;
                if (v.contextId == 0 && free == null) free = v;
                if (v.lastFrame < oldest.lastFrame) oldest = v;
            }
            if (!create) return null;
            ViewSlot slot = free ?? oldest;   // more than kMaxViews contexts: recycle the one that was seen longest ago
            slot.contextId = contextId; slot.dispatches = 0;
            slot.cameraName = camera != null ? camera.name : "(unknown camera)";
            return slot;
        }

        internal static void MarkView(int contextId, Camera camera, Vector2Int render, Vector2Int display)
        {
            ViewSlot v = FindView(contextId, camera, true);
            v.lastFrame = Time.frameCount;
            v.render = render; v.display = display;
        }

        internal static void MarkViewDispatched(int contextId)
        {
            ViewSlot v = FindView(contextId, null, false);
            if (v != null) v.dispatches++;
        }

        /// <summary>Appends the views that were upscaled in the last two frames (clears <paramref name="into"/> first), ordered by context id.</summary>
        public static int GetActiveViews(List<FSR4ViewStats> into)
        {
            into.Clear();
            int frame = Time.frameCount;
            for (int i = 0; i < s_Views.Length; i++)
            {
                ViewSlot v = s_Views[i];
                if (v.contextId != 0 && v.lastFrame >= 0 && frame - v.lastFrame <= 2)
                    into.Add(new FSR4ViewStats(v.cameraName, v.contextId, v.render, v.display, v.dispatches));
            }
            into.Sort((a, b) => a.contextId.CompareTo(b.contextId));
            return into.Count;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetViews()   // domain reload can be disabled in the Editor
        {
            for (int i = 0; i < s_Views.Length; i++)
                s_Views[i] = new ViewSlot();
        }

        internal static void MarkSkipped(string reason)
        {
            lastRecordFrame = Time.frameCount;
            skipReason = reason;
        }
    }
}
#endif
