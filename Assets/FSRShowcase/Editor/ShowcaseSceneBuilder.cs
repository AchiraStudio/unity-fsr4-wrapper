#if ENABLE_UPSCALER_FRAMEWORK
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FSRShowcase.Editor
{
    /// <summary>Builds FSR_Showcase.unity from the Oasis sample scene, leaving the original untouched.</summary>
    public static class ShowcaseSceneBuilder
    {
        public const string ScenePath = ShowcaseSetup.Root + "/Scenes/FSR_Showcase.unity";
        const string SourceScene = "Assets/Scenes/Oasis/OasisScene.unity";
        const string PanelPath = ShowcaseSetup.Root + "/UI/ShowcasePanelSettings.asset";
        const string StylePath = ShowcaseSetup.Root + "/UI/ShowcaseMenu.uss";

        public static string Build()
        {
            var log = new System.Text.StringBuilder();

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
                    return "copy failed";
                AssetDatabase.Refresh();
            }

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1920, 1080);
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                panel.match = 0.5f;
                panel.sortingOrder = 100;
                AssetDatabase.CreateAsset(panel, PanelPath);
            }
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // The sample's intro timeline would fight the showcase for the camera.
            foreach (var d in Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include))
            {
                d.playOnAwake = false;
                log.AppendLine("director playOnAwake off: " + d.name);
            }

            // UI Toolkit needs an event system with the Input System module (the sample ships one, disabled).
            foreach (var m in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
                if (m.GetType().Name == "InputSystemUIInputModule")
                {
                    m.gameObject.SetActive(true);
                    m.enabled = true;
                    var es = m.GetComponent<UnityEngine.EventSystems.EventSystem>();
                    if (es != null) es.enabled = true;
                    log.AppendLine("event system enabled: " + m.name);
                }

            var existing = GameObject.Find("FSR Showcase");
            if (existing != null) Object.DestroyImmediate(existing);

            var root = new GameObject("FSR Showcase");
            var points = new GameObject("PhotoPoints").transform;
            points.SetParent(root.transform, false);

            var start = new Vector3(-2.64f, 3.24f, -94.15f);
            Transform Point(string name, float pitch, float yaw)
            {
                var t = new GameObject(name).transform;
                t.SetParent(points, false);
                t.SetPositionAndRotation(start, Quaternion.Euler(pitch, yaw, 0f));
                return t;
            }
            var p0 = Point("P0 Vista", 0f, 170.8f);
            var p1 = Point("P1 Foliage", 4f, 215f);
            var p2 = Point("P2 Grass and rock", 4f, 125f);

            var ctl = root.AddComponent<ShowcaseController>();
            var so = new SerializedObject(ctl);
            so.FindProperty("m_PanelSettings").objectReferenceValue = panel;
            so.FindProperty("m_Style").objectReferenceValue = style;
            var arr = so.FindProperty("m_PhotoPoints");
            arr.arraySize = 3;
            arr.GetArrayElementAtIndex(0).objectReferenceValue = p0;
            arr.GetArrayElementAtIndex(1).objectReferenceValue = p1;
            arr.GetArrayElementAtIndex(2).objectReferenceValue = p2;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            // Build settings: the showcase scene only, so Player builds stay small and boot straight into it.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            log.AppendLine("scene saved, build settings = showcase only");
            return log.ToString();
        }
    }
}
#endif
