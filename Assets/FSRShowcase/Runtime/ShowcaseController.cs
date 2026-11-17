using System.Collections;
using AchiraStudio.FSR4;
using AchiraStudio.FSR4.DebugHud;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace FSRShowcase
{
    /// <summary>
    /// Scene bootstrap: builds the settings menu, performance overlay and HUD stress test, applies saved settings, handles hotkeys and
    /// the cursor, and starts the automation runner when the Player is launched with <c>-fsr-run</c>.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class ShowcaseController : MonoBehaviour
    {
        public static ShowcaseController Instance { get; private set; }

        [SerializeField] PanelSettings m_PanelSettings;
        [SerializeField] StyleSheet m_Style;
        [SerializeField, Tooltip("Photo points: 0 = vista, 1 = foliage close-up, 2 = grass and rock.")] Transform[] m_PhotoPoints;

        public SettingsMenu Menu { get; private set; }
        public PerfOverlay Perf { get; private set; }
        public SplitScreenRig Split { get; } = new SplitScreenRig();
        public ShowcaseSettings Settings { get; private set; }
        public bool Persist { get; set; } = true;
        public Transform[] PhotoPoints => m_PhotoPoints;
        public bool AutomationControlsCamera { get; private set; }

        HudStressTest m_HudUi;
        HudStressTest.CanvasVariant m_HudCanvas;
        HudStressTest.CanvasVariant m_HudWorld;
        FSR4DebugHud m_DebugHud;
        UIDocument m_Doc;
        MonoBehaviour[] m_PlayerBehaviours;
        float m_NextMenuTick;
        bool m_Ready;

        void Awake()
        {
            Instance = this;
            Settings = ShowcaseSettings.Load();
            foreach (string a in System.Environment.GetCommandLineArgs())
                if (a == "-fsr-run") { Persist = false; Settings = new ShowcaseSettings { fov = 55f }; }
        }

        IEnumerator Start()
        {
            // The render pipeline and the upscaling framework finish initialising during the first frames.
            yield return null;
            yield return null;

            var go = new GameObject("ShowcaseUI");
            go.transform.SetParent(transform, false);
            m_Doc = go.AddComponent<UIDocument>();
            m_Doc.panelSettings = m_PanelSettings;

            Perf = new PerfOverlay(m_Doc.rootVisualElement, m_Style);
            m_HudUi = new HudStressTest(m_Doc.rootVisualElement, m_Style);
            Menu = new SettingsMenu(m_Doc.rootVisualElement, m_Style, Settings)
            {
                meter = Perf.meter,
                getSplitScreen = () => Split.Active,
                setSplitScreen = SetSplitScreen,
                onApply = s => ApplySettings(s, true),
            };

            var dbg = new GameObject("FSR4 Debug HUD");
            dbg.transform.SetParent(transform, false);
            m_DebugHud = dbg.AddComponent<FSR4DebugHud>();

            m_PlayerBehaviours = FindPlayerBehaviours();
            ApplySettings(Settings, true);
            m_Ready = true;
            SetMenuOpen(false);

            string runArg = null, outArg = null;
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-fsr-run") runArg = args[i + 1];
                if (args[i] == "-fsr-out") outArg = args[i + 1];
            }
            if (runArg != null)
            {
                var auto = gameObject.AddComponent<ShowcaseAutomation>();
                auto.Begin(runArg, outArg);
            }
        }

        static MonoBehaviour[] FindPlayerBehaviours()
        {
            var list = new System.Collections.Generic.List<MonoBehaviour>();
            foreach (var m in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
            {
                string n = m.GetType().Name;
                if (n == "FirstPersonController" || n == "StarterAssetsInputs" || n == "PlayerInput" || n == "PlayerManager")
                    list.Add(m);
            }
            return list.ToArray();
        }

        /// <summary>Stops the player rig and Cinemachine so scripts can pose the camera directly.</summary>
        public void TakeCameraControl()
        {
            AutomationControlsCamera = true;
            foreach (var m in m_PlayerBehaviours) if (m != null) m.enabled = false;
            var cam = Camera.main;
            if (cam != null)
            {
                var brain = cam.GetComponent("CinemachineBrain") as Behaviour;
                if (brain != null) brain.enabled = false;
                var crosshair = cam.transform.Find("CrosshairCanvas");
                if (crosshair != null) crosshair.gameObject.SetActive(false);
            }
        }

        public void PoseCamera(int photoPoint)
        {
            var cam = Camera.main;
            if (cam == null || m_PhotoPoints == null || photoPoint >= m_PhotoPoints.Length) return;
            cam.transform.SetPositionAndRotation(m_PhotoPoints[photoPoint].position, m_PhotoPoints[photoPoint].rotation);
        }

        // ------------------------------------------------------------------------------------------------------------------
        public void ApplySettings(ShowcaseSettings s, bool includeDisplay)
        {
            Settings = s.Clone();
            if (Menu != null && !ReferenceEquals(Menu.Applied, s)) { /* menu keeps its own copies */ }
            GraphicsController.Apply(Settings, includeDisplay);
            Perf?.SetVisible(Settings.showPerfOverlay && !(Menu != null && Menu.IsOpen));
            if (m_DebugHud != null) m_DebugHud.visible = Settings.showDebugHud;
            ApplyHudTest(Settings.hudTest);
            if (Persist) Settings.Save();
        }

        /// <summary>Applies settings from code and refreshes the menu so it shows the same values.</summary>
        public void ApplyFromCode(ShowcaseSettings s, bool includeDisplay = false)
        {
            ApplySettings(s, includeDisplay);
            Menu?.ReplaceSettings(Settings);
        }

        void ApplyHudTest(HudTestKind kind)
        {
            m_HudUi?.SetVisible(kind == HudTestKind.OverlayUIToolkit);
            m_HudCanvas?.Destroy(); m_HudCanvas = null;
            m_HudWorld?.Destroy(); m_HudWorld = null;
            var cam = Camera.main;
            if (cam == null) return;
            if (kind == HudTestKind.ScreenSpaceCamera) m_HudCanvas = HudStressTest.CanvasVariant.Create(false, cam);
            if (kind == HudTestKind.WorldSpace) m_HudWorld = HudStressTest.CanvasVariant.Create(true, cam);
        }

        public void SetSplitScreen(bool on)
        {
            if (on)
            {
                Split.Enable(Camera.main, m_PhotoPoints != null && m_PhotoPoints.Length > 2 ? m_PhotoPoints[2] : null);
                GraphicsController.Apply(Settings, false);   // new camera gets the same post-processing choice
            }
            else
                Split.Disable();
        }

        public void SetMenuOpen(bool open)
        {
            if (Menu == null) return;
            Menu.SetOpen(open);
            Perf?.SetVisible(Settings.showPerfOverlay && !open);
            if (m_PlayerBehaviours != null && !AutomationControlsCamera)
                foreach (var m in m_PlayerBehaviours) if (m != null) m.enabled = !open;
            if (!AutomationControlsCamera)
            {
                Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = open;
            }
            else
            {
                Cursor.visible = open;
            }
        }

        void Update()
        {
            if (!m_Ready) return;

            var kb = Keyboard.current;
            if (kb != null && !AutomationControlsCamera)
            {
                if (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) SetMenuOpen(!Menu.IsOpen);
                if (kb.f1Key.wasPressedThisFrame) { Settings.showPerfOverlay = !Settings.showPerfOverlay; ApplyFromCode(Settings); }
                if (kb.f2Key.wasPressedThisFrame) { Settings.showDebugHud = !Settings.showDebugHud; ApplyFromCode(Settings); }
                if (kb.f3Key.wasPressedThisFrame) SetSplitScreen(!Split.Active);
                if (kb.f4Key.wasPressedThisFrame) { Settings.hudTest = (HudTestKind)(((int)Settings.hudTest + 1) % 4); ApplyFromCode(Settings); }
            }

            Perf.Update();
            Split.Update(Time.unscaledDeltaTime);
            m_HudUi.Update();
            m_HudCanvas?.Update();
            m_HudWorld?.Update();

            if (Time.unscaledTime >= m_NextMenuTick)
            {
                m_NextMenuTick = Time.unscaledTime + 0.25f;
                Menu.Tick();
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
