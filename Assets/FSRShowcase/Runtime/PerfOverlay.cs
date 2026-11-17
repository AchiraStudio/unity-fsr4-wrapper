using AchiraStudio.FSR4;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEngine;
using UnityEngine.UIElements;

namespace FSRShowcase
{
    /// <summary>Top-right FPS counter driven by the package's FSR4FrameRateMeter: rendered + generated = presented.</summary>
    public sealed class PerfOverlay
    {
        public readonly FSR4FrameRateMeter meter = new FSR4FrameRateMeter();

        readonly VisualElement m_Root;
        readonly Label m_Big, m_Unit, m_Split, m_Time, m_Upscaler, m_FrameGen;
        float m_NextText;
        float m_SmoothedMs;

        public PerfOverlay(VisualElement host, StyleSheet sheet)
        {
            m_Root = new VisualElement { name = "perfOverlay", pickingMode = PickingMode.Ignore };
            m_Root.AddToClassList("po-root");
            m_Root.styleSheets.Add(sheet);
            host.Add(m_Root);

            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("po-fps");
            m_Big = new Label("0") { pickingMode = PickingMode.Ignore };
            m_Big.AddToClassList("po-big");
            m_Unit = new Label("FPS") { pickingMode = PickingMode.Ignore };
            m_Unit.AddToClassList("po-unit");
            row.Add(m_Big);
            row.Add(m_Unit);
            m_Root.Add(row);

            m_Split = Line("po-line-hl");
            m_Time = Line(null);
            m_Upscaler = Line(null);
            m_FrameGen = Line(null);
        }

        Label Line(string extra)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.AddToClassList("po-line");
            if (extra != null) l.AddToClassList(extra);
            m_Root.Add(l);
            return l;
        }

        public void SetVisible(bool v) => m_Root.EnableInClassList("sm-hidden", !v);

        public void Update()
        {
            meter.Update();
            float ms = Time.unscaledDeltaTime * 1000f;
            m_SmoothedMs = m_SmoothedMs <= 0f ? ms : Mathf.Lerp(m_SmoothedMs, ms, 0.05f);

            if (Time.unscaledTime < m_NextText)
                return;
            m_NextText = Time.unscaledTime + 0.25f;

            bool fg = FSR4FrameGeneration.state == FSR4FrameGenState.Active;
            m_Big.text = Mathf.RoundToInt(meter.presentedFps).ToString();
            m_Unit.text = fg ? "FPS  PRESENTED" : "FPS";
            m_Split.text = $"RENDERED {meter.renderedFps:0.0}  +  GENERATED {meter.generatedFps:0.0}";
            m_Time.text = $"FRAME TIME {m_SmoothedMs:0.0} ms   ({Screen.width}x{Screen.height})";

            if (GraphicsController.WrapperActive && FSR4Stats.renderResolution.x > 0)
                m_Upscaler.text = $"{GraphicsController.DescribeActive()}  {FSR4Stats.renderResolution.x}x{FSR4Stats.renderResolution.y} → {FSR4Stats.displayResolution.x}x{FSR4Stats.displayResolution.y}";
            else
                m_Upscaler.text = GraphicsController.DescribeActive();

            string fgText = FSR4FrameGeneration.enabled ? FSR4FrameGeneration.state.ToString() : "Off";
            if (FSR4FrameGeneration.pausedForSplitScreen) fgText += " (paused: split-screen)";
            m_FrameGen.text = "FRAME GEN  " + fgText;
        }
    }
}
