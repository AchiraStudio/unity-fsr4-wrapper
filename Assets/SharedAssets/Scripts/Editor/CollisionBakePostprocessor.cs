using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

sealed class CollisionBakePostprocessor : AssetPostprocessor
{
    void OnPostprocessModel(GameObject root)
    {
        var settings = GetSettings(assetImporter);
        if (settings == null) return;

        var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in meshFilters)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;

            mesh.SetPreBakeCollisionMesh(false, settings.triangleMeshNames.Contains(mesh.name));
            mesh.SetPreBakeCollisionMesh(true, settings.convexMeshNames.Contains(mesh.name));
        }
    }

    public static PreBakeSettings GetSettings(AssetImporter importer)
    {
        var userData = importer.userData;
        if (string.IsNullOrWhiteSpace(userData))
            return null;

        return JsonUtility.FromJson<PreBakeSettings>(userData) ?? new PreBakeSettings();
    }

    public static void SetSettings(AssetImporter importer, PreBakeSettings settings)
    {
        settings.triangleMeshNames.Sort(string.CompareOrdinal);
        settings.convexMeshNames.Sort(string.CompareOrdinal);
        importer.userData = JsonUtility.ToJson(settings);
    }

    [Serializable]
    public class PreBakeSettings
    {
        public List<string> triangleMeshNames = new();
        public List<string> convexMeshNames = new();

        public List<string> GetMeshNames(bool convex)
        {
            return convex ? convexMeshNames : triangleMeshNames;
        }
    }
}
