using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace FSRShowcase
{
    /// <summary>
    /// Test aid: adds a full-screen shader pass at render resolution whose cost is set from the menu. At 1080p this scene is limited by
    /// the CPU, so the upscaler's quality modes cannot show a gain. With a GPU-bound frame (a heavy game, or this load) they do.
    /// </summary>
    [DisallowMultipleRendererFeature("FSR Showcase GPU Load")]
    public sealed class GpuLoadFeature : ScriptableRendererFeature
    {
        /// <summary>Shader loop iterations per pixel. 0 turns the pass off.</summary>
        public static int Iterations;

        [SerializeField] Shader m_Shader;

        sealed class LoadPass : ScriptableRenderPass
        {
            static readonly int s_Iterations = Shader.PropertyToID("_LoadIterations");
            Material m_Material;

            public LoadPass(Material material)
            {
                m_Material = material;
                profilingSampler = new ProfilingSampler("FSR Showcase GPU load");
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents;   // render resolution, before post-processing and the upscaler
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (Iterations <= 0 || m_Material == null)
                    return;
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();
                if (cameraData.cameraType != CameraType.Game || resourceData.isActiveTargetBackBuffer)
                    return;

                TextureHandle source = resourceData.activeColorTexture;
                if (!source.IsValid())
                    return;
                TextureDesc desc = renderGraph.GetTextureDesc(source);
                desc.name = "_FSRShowcaseGpuLoad";
                desc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(desc);

                m_Material.SetFloat(s_Iterations, Iterations);
                renderGraph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source, destination, m_Material, 0), "FSR Showcase GPU load");
                resourceData.cameraColor = destination;
            }
        }

        Material m_Material;
        LoadPass m_Pass;

        public override void Create()
        {
            if (m_Shader == null)
                return;
            m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
            m_Pass = new LoadPass(m_Material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass != null && Iterations > 0)
                renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
            m_Pass = null;
        }
    }
}
