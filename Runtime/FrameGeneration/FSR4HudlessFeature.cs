#if ENABLE_UPSCALER_FRAMEWORK
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AchiraStudio.FSR4.FrameGeneration
{
    /// <summary>
    /// URP renderer feature that gives frame generation a HUD-less image (the finished frame without the screen-space overlay UI).
    /// Add it to the URP renderer, then set <see cref="FSR4FrameGeneration.hudless"/> (or start the player with <c>-fsr4-hudless</c>).
    /// Experimental, Direct3D12 and SDR output only.
    /// <para>
    /// The pass copies the camera colour after post processing, just before URP's final blit, which is where the overlay UI has
    /// not been drawn yet. FidelityFX compares that copy with the backbuffer to find the UI. The copy therefore has to match
    /// the backbuffer pixel for pixel except for the UI: effects that URP applies in its final post-processing step (FXAA,
    /// dithering, film grain) are missing from it and would be read as UI. Disable them while using this feature.
    /// </para>
    /// </summary>
    [DisallowMultipleRendererFeature("FSR4 HUD-less Capture")]
    public sealed class FSR4HudlessFeature : ScriptableRendererFeature
    {
        sealed class CapturePass : ScriptableRenderPass
        {
            sealed class BlitData
            {
                public TextureHandle source;
                public Camera camera;
            }

            sealed class IssueData
            {
                public TextureHandle target;
                public Camera camera;
                public IntPtr native;
                public uint width, height;
            }

            RenderTexture m_Texture;
            RTHandle m_Handle;
            IntPtr m_Native;
            bool m_WarnedFinalPostProcessing;

            public CapturePass()
            {
                profilingSampler = new ProfilingSampler("FSR4 HUD-less capture");
                // After URP's post processing, before the final blit and the overlay UI.
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            }

            public void Dispose()
            {
                m_Handle?.Release();
                m_Handle = null;
                if (m_Texture != null)
                {
                    m_Texture.Release();
                    if (Application.isPlaying) UnityEngine.Object.Destroy(m_Texture); else UnityEngine.Object.DestroyImmediate(m_Texture);
                    m_Texture = null;
                }
                m_Native = IntPtr.Zero;
            }

            bool EnsureTexture(int width, int height)
            {
                if (m_Texture != null && m_Texture.width == width && m_Texture.height == height && m_Texture.IsCreated())
                    return true;
                Dispose();
                // Same format family as the camera colour target that URP blits into the (sRGB-encoded) backbuffer.
                GraphicsFormat format = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
                m_Texture = new RenderTexture(width, height, 0, format) { name = "FSR4 HUD-less", hideFlags = HideFlags.HideAndDontSave };
                if (!m_Texture.Create())
                {
                    Dispose();
                    return false;
                }
                m_Handle = RTHandles.Alloc(m_Texture);
                m_Native = m_Texture.GetNativeTexturePtr();
                return m_Native != IntPtr.Zero;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                if (cameraData.cameraType != CameraType.Game || !FSR4FrameGeneration.hudlessRequested)
                    return;
                if (resourceData.isActiveTargetBackBuffer)
                    return;   // the final blit already happened (a custom pass swapped to the backbuffer): nothing readable

                TextureHandle source = resourceData.activeColorTexture;
                if (!source.IsValid())
                    return;
                var desc = renderGraph.GetTextureDesc(source);
                if (!EnsureTexture(desc.width, desc.height))
                    return;

                if (!m_WarnedFinalPostProcessing && (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing || cameraData.isDitheringEnabled))
                {
                    m_WarnedFinalPostProcessing = true;
                    Debug.LogWarning("[FSR4 HUD-less] FXAA or dithering is enabled on the camera. URP applies them after this capture, so the HUD-less " +
                                     "image will differ from the backbuffer and frame generation may read the difference as UI. Disable them for HUD-less input.");
                }

                FSR4FrameGeneration.NotifyHudlessFeatureRecorded();
                TextureHandle target = renderGraph.ImportTexture(m_Handle);

                using (var builder = renderGraph.AddRasterRenderPass<BlitData>("FSR4 HUD-less copy", out BlitData data, profilingSampler))
                {
                    data.source = source;
                    data.camera = cameraData.camera;
                    builder.UseTexture(source);
                    builder.SetRenderAttachment(target, 0);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (BlitData d, RasterGraphContext ctx) =>
                    {
                        // Skip the copy in frames where frame generation did not prepare this camera.
                        if (!FSR4FrameGeneration.IsHudlessSourceThisFrame(d.camera))
                            return;
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), 0f, false);
                    });
                }

                using (var builder = renderGraph.AddUnsafePass<IssueData>("FSR4 HUD-less hand-over", out IssueData data, profilingSampler))
                {
                    data.target = target;
                    data.camera = cameraData.camera;
                    data.native = m_Native;
                    data.width = (uint)m_Texture.width;
                    data.height = (uint)m_Texture.height;
                    builder.UseTexture(target);   // orders this after the copy and lets URP put the texture in a readable state
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (IssueData d, UnsafeGraphContext ctx) =>
                    {
                        if (!FSR4FrameGeneration.IsHudlessSourceThisFrame(d.camera))
                            return;
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                        FGNative.IssueHudless(cmd, d.native, d.width, d.height);
                    });
                }
            }
        }

        CapturePass m_Pass;

        public override void Create() => m_Pass = new CapturePass();

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass == null || !FSR4FrameGeneration.hudlessRequested)
                return;
            if (renderingData.cameraData.cameraType != CameraType.Game)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass?.Dispose();
            m_Pass = null;
        }
    }
}
#endif
