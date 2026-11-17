using System;
using System.Collections.Generic;
using AchiraStudio.FSR4;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEngine;
using UnityEngine.UIElements;

namespace FSRShowcase
{
    /// <summary>
    /// Modern graphics menu built with UI Toolkit. Changes are staged and committed with Apply, like a typical AAA settings screen.
    /// Styled by ShowcaseMenu.uss (warm dusk palette to match the Oasis scene).
    /// </summary>
    public sealed class SettingsMenu
    {
        public ShowcaseSettings Pending { get; private set; }
        public ShowcaseSettings Applied { get; private set; }
        public bool IsOpen { get; private set; }

        public Action<ShowcaseSettings> onApply;
        public Func<bool> getSplitScreen;
        public Action<bool> setSplitScreen;
        public FSR4FrameRateMeter meter;

        readonly VisualElement m_Root;
        readonly VisualElement m_Panel;
        readonly VisualElement m_Tabs;
        readonly Label m_Chip;
        readonly Button m_ApplyButton;
        readonly List<Action> m_Refresh = new List<Action>();
        readonly List<Action> m_Live = new List<Action>();
        readonly List<Button> m_TabButtons = new List<Button>();
        readonly List<ScrollView> m_Pages = new List<ScrollView>();
        List<Vector2Int> m_Resolutions;
        int m_Tab;

        static readonly string[] kTabNames = { "DISPLAY", "UPSCALING", "FRAME GEN", "ADVANCED", "OVERLAYS" };
        static readonly string[] kDisplayModes = { "Borderless", "Exclusive Fullscreen", "Windowed" };
        static readonly string[] kFrameCaps = { "Unlimited", "30", "60", "90", "120", "144", "240" };
        static readonly int[] kFrameCapValues = { 0, 30, 60, 90, 120, 144, 240 };
        static readonly string[] kGenerations = { "Auto (newest)", "FSR 4 (ML)", "FSR 3.1", "FSR 2.3" };
        static readonly string[] kQuality = { "Native AA", "Quality", "Balanced", "Performance", "Ultra Perf." };
        static readonly string[] kLegacy = { "Unity STP", "FSR 1.0 (spatial)", "Bilinear", "Point" };
        static readonly string[] kPresets = { "Low", "Medium", "High", "Ultra", "Custom" };
        static readonly string[] kShadowQ = { "Off", "Low", "Medium", "High", "Ultra" };
        static readonly string[] kTexQ = { "Full", "Half", "Quarter" };
        static readonly string[] kAniso = { "Off", "2x", "4x", "8x", "16x" };
        static readonly string[] kHudTest = { "Off", "Overlay UI (after upscale)", "Screen-space camera UI", "World-space UI" };

        public SettingsMenu(VisualElement host, StyleSheet sheet, ShowcaseSettings current)
        {
            Applied = current.Clone();
            Pending = current.Clone();
            BuildResolutionList();

            m_Root = new VisualElement { name = "settingsRoot" };
            m_Root.AddToClassList("sm-root");
            m_Root.styleSheets.Add(sheet);
            host.Add(m_Root);

            m_Panel = new VisualElement { name = "settingsPanel" };
            m_Panel.AddToClassList("sm-panel");
            m_Root.Add(m_Panel);

            // header
            var header = new VisualElement();
            header.AddToClassList("sm-header");
            var titles = new VisualElement();
            var title = new Label("GRAPHICS");
            title.AddToClassList("sm-title");
            var sub = new Label("OASIS  |  FSR 4 WRAPPER SHOWCASE");
            sub.AddToClassList("sm-subtitle");
            titles.Add(title);
            titles.Add(sub);
            m_Chip = new Label("");
            m_Chip.AddToClassList("sm-chip");
            header.Add(titles);
            header.Add(m_Chip);
            m_Panel.Add(header);

            m_Tabs = new VisualElement();
            m_Tabs.AddToClassList("sm-tabs");
            m_Panel.Add(m_Tabs);

            BuildDisplayPage();
            BuildUpscalingPage();
            BuildFrameGenPage();
            BuildAdvancedPage();
            BuildOverlaysPage();

            for (int i = 0; i < kTabNames.Length; i++)
            {
                int idx = i;
                var b = new Button(() => SelectTab(idx)) { text = kTabNames[i] };
                b.AddToClassList("sm-tab");
                m_Tabs.Add(b);
                m_TabButtons.Add(b);
            }

            // footer
            var footer = new VisualElement();
            footer.AddToClassList("sm-footer");
            var hint = new Label("Esc close   F1 perf   F2 debug HUD   F3 split-screen   F4 HUD test");
            hint.AddToClassList("sm-hint");
            footer.Add(hint);
            footer.Add(MakeButton("DEFAULTS", ResetDefaults, false));
            footer.Add(MakeButton("REVERT", Revert, false));
            m_ApplyButton = MakeButton("APPLY", Apply, true);
            footer.Add(m_ApplyButton);
            m_Panel.Add(footer);

            SelectTab(0);
            RefreshAll();
            SetOpen(false);
        }

        // ----------------------------------------------------------------------------------------------------------------
        // public surface
        // ----------------------------------------------------------------------------------------------------------------
        public void SetOpen(bool open)
        {
            IsOpen = open;
            m_Root.EnableInClassList("sm-hidden", !open);
            if (open)
                RefreshAll();
        }

        public void SelectTab(int i)
        {
            m_Tab = Mathf.Clamp(i, 0, kTabNames.Length - 1);
            for (int p = 0; p < m_Pages.Count; p++)
                m_Pages[p].EnableInClassList("sm-hidden", p != m_Tab);
            for (int t = 0; t < m_TabButtons.Count; t++)
                m_TabButtons[t].EnableInClassList("sm-tab-active", t == m_Tab);
        }

        public void ReplaceSettings(ShowcaseSettings s)
        {
            Applied = s.Clone();
            Pending = s.Clone();
            RefreshAll();
        }

        /// <summary>Called a few times per second to refresh read-only status rows.</summary>
        public void Tick()
        {
            if (!IsOpen)
                return;
            foreach (var a in m_Live) a();
            bool dirty = JsonUtility.ToJson(Pending) != JsonUtility.ToJson(Applied);
            m_ApplyButton.EnableInClassList("sm-btn-dirty", dirty);
            m_Chip.text = GraphicsController.DescribeActive().ToUpperInvariant();
        }

        void Apply()
        {
            Applied = Pending.Clone();
            Applied.Save();
            onApply?.Invoke(Applied);
            RefreshAll();
        }

        void Revert() { Pending = Applied.Clone(); RefreshAll(); }

        void ResetDefaults()
        {
            var d = new ShowcaseSettings { width = Pending.width, height = Pending.height };
            Pending = d;
            RefreshAll();
        }

        void RefreshAll()
        {
            foreach (var a in m_Refresh) a();
            foreach (var a in m_Live) a();
        }

        // ----------------------------------------------------------------------------------------------------------------
        // pages
        // ----------------------------------------------------------------------------------------------------------------
        void BuildDisplayPage()
        {
            var p = NewPage();
            Section(p, "WINDOW");
            Selector(p, "Resolution", ResolutionNames(), () => ResolutionIndex(), i => { Pending.width = m_Resolutions[i].x; Pending.height = m_Resolutions[i].y; });
            Selector(p, "Display mode", kDisplayModes, () => Pending.displayMode, i => Pending.displayMode = i);
            Section(p, "FRAME PACING");
            Switch(p, "VSync", () => Pending.vsync == 1, v => Pending.vsync = v ? 1 : 0, "Frame generation needs VSync off on Direct3D 12 and overrides this.");
            Selector(p, "Frame rate cap", kFrameCaps, () => Math.Max(0, Array.IndexOf(kFrameCapValues, Pending.frameCap)), i => Pending.frameCap = kFrameCapValues[i]);
            Section(p, "CAMERA");
            Slider(p, "Field of view", 50f, 100f, () => Pending.fov, v => Pending.fov = v, "0", "°");
        }

        void BuildUpscalingPage()
        {
            var p = NewPage();
            Section(p, "SUPER RESOLUTION");
            Switch(p, "Upscaling", () => Pending.upscalingEnabled, v => Pending.upscalingEnabled = v, "Off renders natively with the engine's own anti-aliasing.");
            Switch(p, "Legacy upscalers", () => Pending.legacyUpscalers, v => Pending.legacyUpscalers = v, "Switch to Unity's built-in STP / FSR 1 / bilinear instead of the FSR wrapper.", () => Pending.upscalingEnabled);

            Selector(p, "FSR version", kGenerations, () => (int)Pending.fsrGeneration, i => Pending.fsrGeneration = (FsrGeneration)i, () => Pending.upscalingEnabled && !Pending.legacyUpscalers,
                     "FSR 4 (ML) needs a supported Radeon GPU; otherwise FSR 3.1 is used.");
            Segmented(p, "Quality", kQuality, () => Pending.quality, i => Pending.quality = i, () => Pending.upscalingEnabled && !Pending.legacyUpscalers);
            Switch(p, "Sharpening", () => Pending.sharpening, v => Pending.sharpening = v, null, () => Pending.upscalingEnabled && !Pending.legacyUpscalers);
            Slider(p, "Sharpness", 0f, 1f, () => Pending.sharpness, v => Pending.sharpness = v, "0.00", "", () => Pending.upscalingEnabled && !Pending.legacyUpscalers && Pending.sharpening);

            Selector(p, "Legacy upscaler", kLegacy, () => (int)Pending.legacyKind, i => Pending.legacyKind = (LegacyUpscaler)i, () => Pending.upscalingEnabled && Pending.legacyUpscalers);
            Slider(p, "Render scale", 0.33f, 1f, () => Pending.renderScale, v => Pending.renderScale = v, "P0", "", () => Pending.upscalingEnabled && Pending.legacyUpscalers, 100f);

            Section(p, "STATUS");
            Info(p, "Active upscaler", () => GraphicsController.DescribeActive());
            Info(p, "Render → display", () => ResolutionText());
            Info(p, "Scale factor", () => ScaleText());
            Info(p, "Why not running", () => WhyNot(), true);
        }

        void BuildFrameGenPage()
        {
            var p = NewPage();
            Section(p, "FRAME GENERATION  (EXPERIMENTAL)");
            Switch(p, "Frame generation", () => Pending.frameGen, v => Pending.frameGen = v,
                   "Player build on Direct3D 12 / Vulkan with the FSR wrapper active. Forces VSync off.", () => !Pending.legacyUpscalers && Pending.upscalingEnabled);
            Switch(p, "Frame pacing", () => Pending.pacing, v => Pending.pacing = v, "Waits half a frame between the generated and the real frame.", () => Pending.frameGen);
            Switch(p, "HUD-less input", () => Pending.hudless, v => Pending.hudless = v, "Hands FidelityFX the frame without UI so animated HUD does not ghost (D3D12, SDR).", () => Pending.frameGen);

            Section(p, "STATUS");
            Info(p, "State", () => FSR4FrameGeneration.state.ToString(), false, () => StateClass());
            Info(p, "Message", () => FSR4FrameGeneration.statusMessage, true);
            Info(p, "Backend / provider", () => FSR4FrameGeneration.backendName + "  /  " + (string.IsNullOrEmpty(FSR4FrameGeneration.providerInUse) ? "-" : FSR4FrameGeneration.providerInUse));
            Info(p, "Rendered fps", () => meter != null ? meter.renderedFps.ToString("0.0") : "-");
            Info(p, "Generated fps", () => meter != null ? meter.generatedFps.ToString("0.0") : "-");
            Info(p, "Presented fps", () => meter != null ? meter.presentedFps.ToString("0.0") : "-");
            Info(p, "HUD-less", () => FSR4FrameGeneration.hudlessStatus, true);
            Info(p, "Split-screen", () => FSR4FrameGeneration.isSplitScreenDetected
                ? $"{FSR4FrameGeneration.screenViewCount} views, generation {(FSR4FrameGeneration.pausedForSplitScreen ? "paused" : "running")}"
                : "single view");
        }

        void BuildAdvancedPage()
        {
            var p = NewPage();
            Section(p, "PRESET");
            Segmented(p, "Quality preset", kPresets, () => Pending.preset, i => { if (i < 4) Pending.ApplyPreset(i); else Pending.preset = 4; });
            Section(p, "SHADOWS");
            Selector(p, "Shadow quality", kShadowQ, () => Pending.shadowQuality, i => { Pending.shadowQuality = i; Pending.preset = 4; });
            Slider(p, "Shadow distance", 20f, 150f, () => Pending.shadowDistance, v => { Pending.shadowDistance = v; Pending.preset = 4; }, "0", " m");
            Switch(p, "Soft shadows", () => Pending.softShadows, v => { Pending.softShadows = v; Pending.preset = 4; });
            Section(p, "TEXTURES AND GEOMETRY");
            Selector(p, "Texture quality", kTexQ, () => Pending.textureQuality, i => { Pending.textureQuality = i; Pending.preset = 4; });
            Selector(p, "Anisotropic filtering", kAniso, () => Pending.anisotropic, i => { Pending.anisotropic = i; Pending.preset = 4; });
            Slider(p, "Level of detail bias", 0.5f, 2f, () => Pending.lodBias, v => { Pending.lodBias = v; Pending.preset = 4; }, "0.0", "x");
            Section(p, "IMAGE");
            Switch(p, "Post-processing", () => Pending.postProcessing, v => { Pending.postProcessing = v; Pending.preset = 4; });
            Switch(p, "HDR rendering", () => Pending.hdr, v => { Pending.hdr = v; Pending.preset = 4; });
        }

        void BuildOverlaysPage()
        {
            var p = NewPage();
            Section(p, "ON-SCREEN INFORMATION");
            Switch(p, "Performance overlay", () => Pending.showPerfOverlay, v => Pending.showPerfOverlay = v, "Rendered / generated / presented frame rate, frame time, upscaler.");
            Switch(p, "FSR debug HUD", () => Pending.showDebugHud, v => Pending.showDebugHud = v, "The package's own monitor: frame-time graph, 1% lows, per-view contexts.");
            Section(p, "TESTING");
            Selector(p, "HUD stress test", kHudTest, () => (int)Pending.hudTest, i => Pending.hudTest = (HudTestKind)i,
                     null, "Animated HUD elements used to look for ghosting with FSR and frame generation.");
            Switch(p, "Split-screen (2 views)", () => getSplitScreen != null && getSplitScreen(), v => setSplitScreen?.Invoke(v),
                   "Two base cameras, each with its own FSR context. Applies immediately.");
        }

        // ----------------------------------------------------------------------------------------------------------------
        // control factories
        // ----------------------------------------------------------------------------------------------------------------
        ScrollView NewPage()
        {
            var sv = new ScrollView(ScrollViewMode.Vertical);
            sv.AddToClassList("sm-scroll");
            sv.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            m_Panel.Insert(m_Panel.IndexOf(m_Tabs) + 1 + m_Pages.Count, sv);
            m_Pages.Add(sv);
            return sv;
        }

        static void Section(VisualElement page, string text)
        {
            var l = new Label(text);
            l.AddToClassList("sm-section");
            page.Add(l);
        }

        VisualElement NewRow(VisualElement page, string label, string hint, Func<bool> visible, out VisualElement ctrl)
        {
            var row = new VisualElement();
            row.AddToClassList("sm-row");
            var textCol = new VisualElement();
            textCol.AddToClassList("sm-row-text");
            var l = new Label(label);
            l.AddToClassList("sm-row-label");
            textCol.Add(l);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new Label(hint);
                h.AddToClassList("sm-row-hint");
                textCol.Add(h);
            }
            row.Add(textCol);
            ctrl = new VisualElement();
            ctrl.AddToClassList("sm-ctrl");
            row.Add(ctrl);
            page.Add(row);
            if (visible != null)
            {
                var r = row;
                m_Refresh.Add(() => r.EnableInClassList("sm-hidden", !visible()));
            }
            return row;
        }

        void Selector(VisualElement page, string label, string[] options, Func<int> get, Action<int> set, Func<bool> visible = null, string hint = null)
        {
            NewRow(page, label, hint, visible, out var ctrl);
            var value = new Label();
            value.AddToClassList("sm-value");
            void Step(int d)
            {
                int n = options.Length;
                set(((get() + d) % n + n) % n);
                RefreshAll();
            }
            var left = new Button(() => Step(-1)) { text = "<" };
            var right = new Button(() => Step(1)) { text = ">" };
            left.AddToClassList("sm-arrow");
            right.AddToClassList("sm-arrow");
            ctrl.Add(left);
            ctrl.Add(value);
            ctrl.Add(right);
            m_Refresh.Add(() => value.text = options[Mathf.Clamp(get(), 0, options.Length - 1)]);
        }

        void Selector(VisualElement page, string label, Func<string[]> optionsFn, Func<int> get, Action<int> set)
        {
            var options = optionsFn();
            Selector(page, label, options, get, set);
        }

        void Switch(VisualElement page, string label, Func<bool> get, Action<bool> set, string hint = null, Func<bool> visible = null)
        {
            NewRow(page, label, hint, visible, out var ctrl);
            var sw = new VisualElement();
            sw.AddToClassList("sm-switch");
            var knob = new VisualElement();
            knob.AddToClassList("sm-knob");
            sw.Add(knob);
            sw.RegisterCallback<ClickEvent>(_ => { set(!get()); RefreshAll(); });
            ctrl.Add(sw);
            m_Refresh.Add(() => sw.EnableInClassList("sm-switch-on", get()));
        }

        void Slider(VisualElement page, string label, float min, float max, Func<float> get, Action<float> set, string format, string suffix,
                    Func<bool> visible = null, float displayScale = 1f)
        {
            NewRow(page, label, null, visible, out var ctrl);
            var hit = new VisualElement();
            hit.AddToClassList("sm-slider-hit");
            var track = new VisualElement { pickingMode = PickingMode.Ignore };
            track.AddToClassList("sm-slider-track");
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("sm-slider-fill");
            var handle = new VisualElement { pickingMode = PickingMode.Ignore };
            handle.AddToClassList("sm-slider-handle");
            track.Add(fill);
            track.Add(handle);
            hit.Add(track);
            var value = new Label();
            value.AddToClassList("sm-slider-value");
            ctrl.Add(hit);
            ctrl.Add(value);

            void FromPointer(Vector2 worldPos)
            {
                float w = track.worldBound.width;
                if (w <= 1f) return;
                float t = Mathf.Clamp01((worldPos.x - track.worldBound.xMin) / w);
                set(Mathf.Lerp(min, max, t));
                RefreshAll();
            }
            hit.RegisterCallback<PointerDownEvent>(e => { hit.CapturePointer(e.pointerId); FromPointer(e.position); e.StopPropagation(); });
            hit.RegisterCallback<PointerMoveEvent>(e => { if (hit.HasPointerCapture(e.pointerId)) FromPointer(e.position); });
            hit.RegisterCallback<PointerUpEvent>(e => { if (hit.HasPointerCapture(e.pointerId)) hit.ReleasePointer(e.pointerId); });

            m_Refresh.Add(() =>
            {
                float t = Mathf.InverseLerp(min, max, get());
                fill.style.width = Length.Percent(t * 100f);
                handle.style.left = Length.Percent(t * 100f);
                float shown = get() * displayScale;
                value.text = (format == "P0" ? Mathf.RoundToInt(get() * 100f) + "%" : shown.ToString(format)) + (format == "P0" ? "" : suffix);
            });
        }

        void Segmented(VisualElement page, string label, string[] options, Func<int> get, Action<int> set, Func<bool> visible = null)
        {
            NewRow(page, label, null, visible, out var ctrl);
            ctrl.AddToClassList("sm-ctrl-wide");
            ctrl.AddToClassList("sm-seg");
            var buttons = new List<Button>();
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = new Button(() => { set(idx); RefreshAll(); }) { text = options[i] };
                b.AddToClassList("sm-seg-btn");
                ctrl.Add(b);
                buttons.Add(b);
            }
            m_Refresh.Add(() =>
            {
                for (int i = 0; i < buttons.Count; i++)
                    buttons[i].EnableInClassList("sm-seg-active", i == get());
            });
        }

        void Info(VisualElement page, string label, Func<string> get, bool multiline = false, Func<string> cssClass = null)
        {
            NewRow(page, label, null, null, out var ctrl);
            var v = new Label();
            v.AddToClassList("sm-info-value");
            ctrl.Add(v);
            m_Live.Add(() =>
            {
                string t = get();
                v.text = string.IsNullOrEmpty(t) ? "-" : t;
                v.EnableInClassList("sm-good", false);
                v.EnableInClassList("sm-warn", false);
                v.EnableInClassList("sm-bad", false);
                string c = cssClass?.Invoke();
                if (!string.IsNullOrEmpty(c)) v.AddToClassList(c);
            });
        }

        static Button MakeButton(string text, Action click, bool primary)
        {
            var b = new Button(click) { text = text };
            b.AddToClassList("sm-btn");
            if (primary) b.AddToClassList("sm-btn-primary");
            return b;
        }

        // ----------------------------------------------------------------------------------------------------------------
        // helpers
        // ----------------------------------------------------------------------------------------------------------------
        void BuildResolutionList()
        {
            m_Resolutions = new List<Vector2Int>();
            var seen = new HashSet<Vector2Int>();
            foreach (var r in Screen.resolutions)
            {
                var v = new Vector2Int(r.width, r.height);
                if (v.x >= 1280 && v.y >= 720 && seen.Add(v)) m_Resolutions.Add(v);
            }
            foreach (var v in new[] { new Vector2Int(1280, 720), new Vector2Int(1600, 900), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3840, 2160) })
                if (seen.Add(v)) m_Resolutions.Add(v);
            m_Resolutions.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        }

        Func<string[]> ResolutionNames() => () =>
        {
            var n = new string[m_Resolutions.Count];
            for (int i = 0; i < n.Length; i++) n[i] = m_Resolutions[i].x + " x " + m_Resolutions[i].y;
            return n;
        };

        int ResolutionIndex()
        {
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < m_Resolutions.Count; i++)
            {
                int d = Mathf.Abs(m_Resolutions[i].x - Pending.width) + Mathf.Abs(m_Resolutions[i].y - Pending.height);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        static string ResolutionText()
        {
            if (GraphicsController.WrapperActive && FSR4Stats.renderResolution.x > 0)
                return $"{FSR4Stats.renderResolution.x}x{FSR4Stats.renderResolution.y} → {FSR4Stats.displayResolution.x}x{FSR4Stats.displayResolution.y}";
            var a = GraphicsController.Asset;
            float s = a != null ? a.renderScale : 1f;
            return $"{Mathf.RoundToInt(Screen.width * s)}x{Mathf.RoundToInt(Screen.height * s)} → {Screen.width}x{Screen.height}";
        }

        static string ScaleText()
        {
            if (GraphicsController.WrapperActive && FSR4Stats.renderResolution.x > 0 && FSR4Stats.displayResolution.x > 0)
            {
                float r = (float)FSR4Stats.displayResolution.x / FSR4Stats.renderResolution.x;
                return $"{r:0.00}x upscale ({100f / r:0}% per axis)";
            }
            var a = GraphicsController.Asset;
            return a != null ? $"{a.renderScale * 100f:0}% render scale" : "-";
        }

        static string WhyNot()
        {
            if (!GraphicsController.WrapperActive)
                return string.Empty;
            if (!string.IsNullOrEmpty(FSR4Stats.notReadyReason)) return FSR4Stats.notReadyReason;
            return FSR4Stats.skipReason;
        }

        static string StateClass()
        {
            switch (FSR4FrameGeneration.state)
            {
                case FSR4FrameGenState.Active: return "sm-good";
                case FSR4FrameGenState.Idle: return "";
                case FSR4FrameGenState.NoSwapchain: return "sm-warn";
                default: return "sm-bad";
            }
        }
    }
}
