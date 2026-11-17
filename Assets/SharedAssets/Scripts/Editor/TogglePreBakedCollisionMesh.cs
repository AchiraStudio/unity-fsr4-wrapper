using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Pool;

static class TogglePreBakedCollisionMesh
{
    const string k_TriangleMenuPath = "Assets/Tools/Mesh/Toggle Pre-Baked Triangle Mesh";
    const string k_ConvexMenuPath = "Assets/Tools/Mesh/Toggle Pre-Baked Convex Mesh";

    [MenuItem(k_TriangleMenuPath, priority = 1)]
    static void ToggleTriangle()
    {
        Toggle(false);
    }

    [MenuItem(k_ConvexMenuPath, priority = 2)]
    static void ToggleConvex()
    {
        Toggle(true);
    }

    [MenuItem(k_TriangleMenuPath, true)]
    [MenuItem(k_ConvexMenuPath, true)]
    static bool Validate()
    {
        using var _ = ListPool<Mesh>.Get(out var meshes);
        GetSelectedModelMeshes(meshes);
        return meshes.Count > 0;
    }

    static void Toggle(bool convex)
    {
        var meshesByImporter = new Dictionary<ModelImporter, List<string>>();

        using var _ = ListPool<Mesh>.Get(out var meshes);
        GetSelectedModelMeshes(meshes);

        foreach (var mesh in meshes)
        {
            var assetPath = AssetDatabase.GetAssetPath(mesh);
            var importer = (ModelImporter)AssetImporter.GetAtPath(assetPath);
            if (!meshesByImporter.TryGetValue(importer, out var meshNames))
            {
                meshNames = new List<string>();
                meshesByImporter.Add(importer, meshNames);
            }

            meshNames.Add(mesh.name);
        }

        var kind = convex ? "convex" : "triangle";
        foreach (var (importer, meshNames) in meshesByImporter)
        {
            var settings = CollisionBakePostprocessor.GetSettings(importer) ??
                           new CollisionBakePostprocessor.PreBakeSettings();

            var enabledNames = settings.GetMeshNames(convex);

            // enable all if at least one is off, otherwise disable all
            var enable = meshNames.Exists(n => !enabledNames.Contains(n));
            foreach (var meshName in meshNames)
                if (!enable)
                    enabledNames.Remove(meshName);
                else if (!enabledNames.Contains(meshName))
                    enabledNames.Add(meshName);

            CollisionBakePostprocessor.SetSettings(importer, settings);
            importer.SaveAndReimport();
            Debug.LogFormat("{0} pre-baked {1} collision mesh on {2} in {3}", enable ? "Enabled" : "Disabled", kind,
                string.Join(", ", meshNames), importer.assetPath);
        }
    }

    static void GetSelectedModelMeshes(List<Mesh> meshes)
    {
        foreach (var obj in Selection.objects)
            if (obj is Mesh mesh &&
                AssetDatabase.IsSubAsset(mesh) &&
                AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) is ModelImporter)
                meshes.Add(mesh);
    }
}
