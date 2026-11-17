#if ENABLE_UPSCALER_FRAMEWORK
using System.Collections.Generic;
using System.Reflection;
using AchiraStudio.FSR4;
using AchiraStudio.FSR4.FrameGeneration;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FSRShowcase.Editor
{
    /// <summary>One-shot project setup for the FSR showcase: URP asset with the wrapper, HUD-less capture feature, quality levels.</summary>
    public static class ShowcaseSetup
    {
        public const string Root = "Assets/FSRShowcase";
        public const string UrpPath = Root + "/Settings/FSR_Showcase_URP.asset";
        public const string RendererPath = Root + "/Settings/FSR_Showcase_Renderer.asset";

        public static string CreateUrpAsset()
        {
            var log = new System.Text.StringBuilder();

            if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpPath) == null)
            {
                AssetDatabase.CopyAsset("Assets/Settings/PC/PC_High.asset", UrpPath);
                AssetDatabase.CopyAsset("Assets/Settings/PC/PC_High_Renderer.asset", RendererPath);
                AssetDatabase.Refresh();
            }

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpPath);
            var renderer = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);

            // --- renderer: add the HUD-less capture feature once -----------------------------------------------------------
            bool hasFeature = false;
            foreach (var f in renderer.rendererFeatures)
                if (f is FSR4HudlessFeature) hasFeature = true;
            if (!hasFeature)
            {
                var feature = ScriptableObject.CreateInstance<FSR4HudlessFeature>();
                feature.name = "FSR4 HUD-less Capture";
                AssetDatabase.AddObjectToAsset(feature, renderer);
                var rso = new SerializedObject(renderer);
                var list = rso.FindProperty("m_RendererFeatures");
                list.arraySize++;
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
                rso.ApplyModifiedPropertiesWithoutUndo();
                typeof(ScriptableRendererData).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(renderer, null);
                EditorUtility.SetDirty(renderer);
                log.AppendLine("added FSR4HudlessFeature");
            }

            // --- URP asset: upscaling on, our renderer, MSAA off ------------------------------------------------------------
            var so = new SerializedObject(urp);
            var rl = so.FindProperty("m_RendererDataList");
            rl.arraySize = 1;
            rl.GetArrayElementAtIndex(0).objectReferenceValue = renderer;
            so.FindProperty("m_DefaultRendererIndex").intValue = 0;
            so.FindProperty("m_ScalingMode").intValue = 1;          // ScalingMode.Upscaling
            so.FindProperty("m_MSAA").intValue = 1;                  // off: FSR replaces it
            so.FindProperty("m_RenderScale").floatValue = 1f;

            // Priority list: the wrapper first, then Unity's own upscalers so the in-game menu can switch to them.
            string[] ids = { "achirastudio.fsr4", "unity.stp", "amd.fsr1", "unity.bilinear", "unity.point", "unity.auto" };
            var prio = so.FindProperty("m_UpscalerPriority");
            prio.arraySize = 0;
            var valid = new List<string>();
            foreach (var id in ids)
            {
                if (!UpscalerRegistry.s_RegisteredUpscalers.TryGetValue(id, out var reg)) { log.AppendLine("not registered: " + id); continue; }
                prio.arraySize++;
                var e = prio.GetArrayElementAtIndex(prio.arraySize - 1);
                e.FindPropertyRelative("upscalerId").stringValue = id;
                e.FindPropertyRelative("upscalerName").stringValue = reg.DisplayName;
                valid.Add(id);
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            so.Update();
            bool changed = UpscalerOptions.ValidateSerializedUpscalerOptionReferencesWithinRPAsset(urp, so.FindProperty("m_UpscalerOptions"), valid);
            so.ApplyModifiedPropertiesWithoutUndo();

            // FSR wrapper options: Quality, provider auto, resolution mode = quality mode.
            foreach (var o in urp.upscalerOptions)
            {
                if (o is FSR4Options fo)
                {
                    fo.resolutionMode = UpscalerResolutionMode.QualityMode;
                    fo.qualityMode = FSR4Quality.Quality;
                    fo.provider = FSR4Provider.Auto;
                    fo.enableSharpening = false;
                    EditorUtility.SetDirty(fo);
                }
                log.AppendLine("options: " + o.GetType().Name + " (" + o.upscalerId + ")");
            }

            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();

            // --- Quality levels: both PC levels use the showcase asset ---------------------------------------------------------
            var qs = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
            var levels = qs.FindProperty("m_QualitySettings");
            for (int i = 2; i <= 3 && i < levels.arraySize; i++)
                levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline").objectReferenceValue = urp;
            qs.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            log.AppendLine("optionsChanged=" + changed + " prio=" + string.Join(",", valid));
            return log.ToString();
        }
    }
}
#endif
