#if ENABLE_UPSCALER_FRAMEWORK
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4
{
    /// <summary>
    /// Everything the upscaler pass knows about the frame it just upscaled. Handed to <see cref="FSR4Hooks.UpscaleDispatched"/>
    /// listeners (the optional frame-generation module uses it) while the render graph executes, so the native pointers are
    /// only valid for the duration of the callback and may only be used by commands added to the same command buffer.
    /// </summary>
    public struct FSR4FrameData
    {
        /// <summary>Native depth texture (ID3D12Resource* / VkImage) at render resolution.</summary>
        public IntPtr depth;
        /// <summary>Native motion-vector texture at render (or display) resolution.</summary>
        public IntPtr motionVectors;
        /// <summary>Native upscaled colour output at display resolution (before post-upscale effects and UI).</summary>
        public IntPtr color;

        public Vector2Int renderResolution;
        public Vector2Int maxRenderResolution;
        public Vector2Int displayResolution;

        /// <summary>Sub-pixel jitter exactly as passed to the upscaler (sign already applied).</summary>
        public Vector2 jitter;
        /// <summary>Factor that converts the motion-vector texture to pixels (sign already applied), as passed to the upscaler.</summary>
        public Vector2 motionVectorScale;

        public float frameTimeMs;
        public float cameraNear;
        public float cameraFar;
        /// <summary>Vertical field of view in radians.</summary>
        public float cameraFovY;

        public bool resetHistory;
        public bool depthInverted;
        public bool displayResolutionMotionVectors;
        public bool motionVectorJitterCancelled;

        public Vector3 cameraPosition;
        public Vector3 cameraUp;
        public Vector3 cameraRight;
        public Vector3 cameraForward;
    }

    public delegate void UpscaleDispatchedHandler(CommandBuffer cmd, in FSR4FrameData frame);

    /// <summary>
    /// Extension points of the core upscaler. The core package does not depend on any optional module; modules plug in here.
    /// </summary>
    public static class FSR4Hooks
    {
        /// <summary>
        /// Raised from inside the render graph right after the upscale dispatch was issued on <c>cmd</c>.
        /// Only raised while at least one listener is subscribed (no cost otherwise).
        /// </summary>
        public static event UpscaleDispatchedHandler UpscaleDispatched;

        internal static bool hasUpscaleListeners => UpscaleDispatched != null;

        internal static void RaiseUpscaleDispatched(CommandBuffer cmd, in FSR4FrameData frame)
            => UpscaleDispatched?.Invoke(cmd, in frame);
    }

    /// <summary>A block of controls that optional modules add to the in-game debug HUD (see <see cref="FSR4HudSections"/>).</summary>
    public interface IFSR4HudSection
    {
        string Title { get; }
        /// <summary>Lower values are drawn first.</summary>
        int Order { get; }
        /// <summary>Called inside the HUD window's GUILayout (IMGUI).</summary>
        void DrawGUI();
    }

    public static class FSR4HudSections
    {
        static readonly List<IFSR4HudSection> s_Sections = new List<IFSR4HudSection>();

        public static IReadOnlyList<IFSR4HudSection> All => s_Sections;

        public static void Register(IFSR4HudSection section)
        {
            if (section == null || s_Sections.Contains(section))
                return;
            s_Sections.Add(section);
            s_Sections.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        public static void Unregister(IFSR4HudSection section) => s_Sections.Remove(section);
    }

    /// <summary>Read-only information about the native upscaler for UIs and tools (HUD, bug reports).</summary>
    public static class FSR4Diagnostics
    {
        /// <summary>"Direct3D12", "Vulkan", "none" or "unknown".</summary>
        public static string BackendName() => FSR4Native.BackendName();

        /// <summary>Name of the FSR provider of the most recently dispatched context, e.g. "4.1.2" (empty until reported).</summary>
        public static string ProviderInUse() => FSR4Native.TryGetAnyContextVersion();

        public static bool TryGetContextMemory(out ulong totalBytes, out ulong aliasableBytes)
            => FSR4Native.TryGetContextMemory(out totalBytes, out aliasableBytes);

        public static bool TryGetDispatchCounts(out ulong attempts, out ulong executed)
            => FSR4Native.TryGetDispatchCounts(out attempts, out executed);
    }
}
#endif
