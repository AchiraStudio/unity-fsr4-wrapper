using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Image = UnityEngine.UI.Image;

namespace FSRShowcase
{
    /// <summary>
    /// Animated HUD used to look for ghosting. Every element moves at a fixed speed so artefacts show as duplicated or smeared copies.
    /// Three variants: UI Toolkit overlay (drawn after the upscaler), and uGUI in screen-space-camera / world-space (drawn inside the
    /// upscaled image, so FSR's temporal history sees it).
    /// </summary>
    public sealed class HudStressTest
    {
        // ---- shared motion functions (pure, so the three variants move identically) ----
        public static float Triangle(float t, float period) { float x = (t % period) / period; return x < 0.5f ? x * 2f : 2f - x * 2f; }
        public static float Wrap(float t, float period) => (t % period) / period;

        // ---------------------------------------------------------------------------------------------------------------
        // UI Toolkit overlay
        // ---------------------------------------------------------------------------------------------------------------
        readonly VisualElement m_Root;
        readonly VisualElement m_LaneA, m_LaneB, m_LaneC, m_LaneD;
        readonly VisualElement m_Ball, m_Marker, m_Bar, m_Radar, m_Hand;
        readonly Label m_Ticker;

        public HudStressTest(VisualElement host, StyleSheet sheet)
        {
            m_Root = new VisualElement { name = "hudStress", pickingMode = PickingMode.Ignore };
            m_Root.AddToClassList("ht-root");
            m_Root.styleSheets.Add(sheet);
            host.Add(m_Root);

            var title = new Label("HUD STRESS TEST  |  OVERLAY UI") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("ht-title");
            m_Root.Add(title);

            m_LaneA = Lane(); m_Ball = Child(m_LaneA, "ht-ball");
            m_LaneB = Lane(); m_Ticker = new Label("ARMOR 100   AMMO 24/90   OBJECTIVE: REACH THE OASIS   +250 XP") { pickingMode = PickingMode.Ignore };
            m_Ticker.AddToClassList("ht-ticker");
            m_LaneB.Add(m_Ticker);
            m_LaneC = Lane(); m_Marker = Child(m_LaneC, "ht-marker");
            m_LaneD = Lane(); m_Bar = Child(m_LaneD, "ht-bar");
            m_Radar = Child(m_LaneD, "ht-radar");
            m_Hand = Child(m_Radar, "ht-radar-hand");
        }

        VisualElement Lane()
        {
            var l = new VisualElement { pickingMode = PickingMode.Ignore };
            l.AddToClassList("ht-lane");
            m_Root.Add(l);
            return l;
        }

        static VisualElement Child(VisualElement parent, string cls)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList(cls);
            parent.Add(e);
            return e;
        }

        public void SetVisible(bool v) => m_Root.EnableInClassList("sm-hidden", !v);

        public void Update()
        {
            float t = Time.unscaledTime;
            float w = Mathf.Max(100f, m_LaneA.resolvedStyle.width);
            m_Ball.style.left = Triangle(t, 2f) * (w - 32f);                       // ~ 330 px/s at 700 px
            m_Marker.style.left = Wrap(t, 1.2f) * w;                               // fast sweeping marker
            m_Ticker.style.left = w - Wrap(t, 5f) * (w + 760f);                    // scrolling text
            m_Bar.style.width = Length.Percent(Mathf.Lerp(8f, 100f, Triangle(t, 3f)) );
            m_Hand.style.rotate = new Rotate(new Angle(t * 240f, AngleUnit.Degree)); // radar sweep
        }

        // ---------------------------------------------------------------------------------------------------------------
        // uGUI variants
        // ---------------------------------------------------------------------------------------------------------------
        public sealed class CanvasVariant
        {
            public readonly GameObject root;
            readonly RectTransform m_Ball, m_Marker, m_Bar, m_Hand, m_Lane;
            readonly RectTransform m_Ticker;
            readonly float m_Width;

            CanvasVariant(GameObject go, RectTransform lane, float width, RectTransform ball, RectTransform marker, RectTransform ticker, RectTransform bar, RectTransform hand)
            {
                root = go; m_Lane = lane; m_Width = width; m_Ball = ball; m_Marker = marker; m_Ticker = ticker; m_Bar = bar; m_Hand = hand;
            }

            static RectTransform Rect(string name, Transform parent, Color c, Vector2 size, Vector2 anchor)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = anchor;
                rt.pivot = anchor;
                rt.sizeDelta = size;
                go.GetComponent<Image>().color = c;
                return rt;
            }

            /// <summary>Creates a canvas near the bottom left with the same animated elements as the UI Toolkit version.</summary>
            public static CanvasVariant Create(bool worldSpace, Camera cam)
            {
                var go = new GameObject(worldSpace ? "HudStress_WorldSpace" : "HudStress_ScreenSpaceCamera", typeof(Canvas), typeof(CanvasScaler));
                var canvas = go.GetComponent<Canvas>();
                float width = 700f;
                if (worldSpace)
                {
                    canvas.renderMode = RenderMode.WorldSpace;
                    var rt = (RectTransform)go.transform;
                    rt.sizeDelta = new Vector2(width + 24f, 260f);
                    go.transform.SetParent(cam.transform, false);
                    go.transform.localPosition = new Vector3(-0.95f, -0.62f, 2.2f);
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one * 0.0026f;
                }
                else
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 1f;
                    var scaler = go.GetComponent<CanvasScaler>();
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                }

                var panel = Rect("Panel", go.transform, new Color(0.04f, 0.04f, 0.06f, 0.78f), new Vector2(width + 24f, 260f), new Vector2(0f, 0f));
                if (!worldSpace) panel.anchoredPosition = new Vector2(24f, 24f);
                else { panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f); panel.pivot = new Vector2(0.5f, 0.5f); panel.anchoredPosition = Vector2.zero; }

                var title = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI));
                title.transform.SetParent(panel, false);
                var tt = title.GetComponent<TextMeshProUGUI>();
                tt.text = worldSpace ? "HUD STRESS TEST  |  WORLD-SPACE UI" : "HUD STRESS TEST  |  SCREEN-SPACE CAMERA UI";
                tt.fontSize = 14; tt.color = new Color(1f, 0.84f, 0.63f); tt.fontStyle = FontStyles.Bold;
                var trt = (RectTransform)title.transform;
                trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(0, 1);
                trt.anchoredPosition = new Vector2(12, -8); trt.sizeDelta = new Vector2(-24, 22);

                RectTransform LaneRect(int i, out RectTransform lane)
                {
                    lane = Rect("Lane" + i, panel, new Color(1, 1, 1, 0.07f), new Vector2(width, 46f), new Vector2(0, 1));
                    lane.anchoredPosition = new Vector2(12f, -34f - i * 54f);
                    var mask = lane.gameObject.AddComponent<RectMask2D>();
                    return lane;
                }

                LaneRect(0, out var laneA);
                var ball = Rect("Ball", laneA, Color.white, new Vector2(32, 32), new Vector2(0, 0.5f));
                LaneRect(1, out var laneB);
                var tickGo = new GameObject("Ticker", typeof(RectTransform), typeof(TextMeshProUGUI));
                tickGo.transform.SetParent(laneB, false);
                var tick = tickGo.GetComponent<TextMeshProUGUI>();
                tick.text = "ARMOR 100   AMMO 24/90   OBJECTIVE: REACH THE OASIS   +250 XP";
                tick.fontSize = 22; tick.fontStyle = FontStyles.Bold; tick.color = Color.white; tick.textWrappingMode = TextWrappingModes.NoWrap;
                var tickRt = (RectTransform)tickGo.transform;
                tickRt.anchorMin = new Vector2(0, 0.5f); tickRt.anchorMax = new Vector2(0, 0.5f); tickRt.pivot = new Vector2(0, 0.5f); tickRt.sizeDelta = new Vector2(760, 40);
                LaneRect(2, out var laneC);
                var marker = Rect("Marker", laneC, new Color(1f, 0.31f, 0.24f), new Vector2(6, 46), new Vector2(0, 0.5f));
                LaneRect(3, out var laneD);
                var bar = Rect("Bar", laneD, new Color(0.47f, 0.86f, 0.51f), new Vector2(width, 46), new Vector2(0, 0.5f));
                var radar = Rect("Radar", laneD, new Color(1, 1, 1, 0.25f), new Vector2(38, 38), new Vector2(0.5f, 0.5f));
                var hand = Rect("Hand", radar, new Color(1f, 0.31f, 0.24f), new Vector2(4, 17), new Vector2(0.5f, 0.5f));
                hand.pivot = new Vector2(0.5f, 0f);
                hand.anchoredPosition = Vector2.zero;
                return new CanvasVariant(go, laneA, width, ball, marker, tickRt, bar, hand);
            }

            public void Update()
            {
                float t = Time.unscaledTime;
                m_Ball.anchoredPosition = new Vector2(Triangle(t, 2f) * (m_Width - 32f), 0f);
                m_Marker.anchoredPosition = new Vector2(Wrap(t, 1.2f) * m_Width, 0f);
                m_Ticker.anchoredPosition = new Vector2(m_Width - Wrap(t, 5f) * (m_Width + 760f), 0f);
                m_Bar.sizeDelta = new Vector2(m_Width * Mathf.Lerp(0.08f, 1f, Triangle(t, 3f)), 46f);
                m_Hand.localRotation = Quaternion.Euler(0f, 0f, -t * 240f);
            }

            public void Destroy() { if (root != null) Object.Destroy(root); }
        }
    }
}
