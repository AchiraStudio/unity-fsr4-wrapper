using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

static class ListCollisionMeshAssets
{
    [MenuItem("Tools/List Mesh Assets Used By Colliders in Open Scenes")]
    static void List()
    {
        var colliders = new List<(string assetPath, MeshCollider collider)>();

        for (var i = 0; i < SceneManager.sceneCount; ++i)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (var root in scene.GetRootGameObjects())
            foreach (var meshCollider in root.GetComponentsInChildren<MeshCollider>(true))
            {
                var mesh = meshCollider.sharedMesh;
                if (mesh == null) continue;

                var path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path))
                    path = $"(built-in or scene mesh) {mesh.name}";

                colliders.Add((path, meshCollider));
            }
        }

        colliders.Sort((a, b) => string.CompareOrdinal(a.assetPath, b.assetPath));
        foreach (var (assetPath, collider) in colliders)
            Debug.Log(assetPath, collider.gameObject);
    }
}
