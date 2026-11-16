using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace AchiraStudio.FSR4.Editor
{
    /// <summary>
    /// Opt-in setup helpers. Nothing here runs automatically; every item only changes project settings when clicked.
    /// </summary>
    static class FSR4Setup
    {
        const string Define = "ENABLE_UPSCALER_FRAMEWORK";
        const string Root = "Tools/AchiraStudio/FSR4/";
        const string PackageName = "com.achirastudio.fsr4";

        /// <summary>Where the package lives on disk (works for embedded, local, git and registry installs).</summary>
        static string PackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(FSR4Setup).Assembly);
            return info != null ? info.resolvedPath : Path.GetFullPath("Packages/" + PackageName);
        }

        [MenuItem(Root + "1. Enable Upscaler Framework define (Windows Standalone)")]
        static void EnableDefine()
        {
            var target = NamedBuildTarget.Standalone;
            string current = PlayerSettings.GetScriptingDefineSymbols(target);
            if (HasDefine(current))
            {
                Debug.Log($"[FSR4] {Define} is already set for Standalone.");
                return;
            }
            PlayerSettings.SetScriptingDefineSymbols(target, string.IsNullOrEmpty(current) ? Define : current + ";" + Define);
            Debug.Log($"[FSR4] Added {Define} to Standalone scripting defines. Scripts will recompile.");
        }

        [MenuItem(Root + "2. Use Direct3D12 only (Windows Standalone)")]
        static void UseD3D12()
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
            Debug.Log("[FSR4] Windows graphics API set to Direct3D12. Restart the Editor so the Editor itself also runs on D3D12 " +
                      "(Preferences > or launch with -force-d3d12).");
        }

        [MenuItem(Root + "2b. Use Vulkan only (Windows Standalone) - experimental")]
        static void UseVulkan()
        {
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Vulkan });
            Debug.Log("[FSR4] Windows graphics API set to Vulkan (experimental FSR backend). Restart the Editor with -force-vulkan to test it in the Editor. " +
                      "Note: FSR 4 (ML) may not be available on Vulkan inside Unity - see README.");
        }

        [MenuItem(Root + "Check status")]
        static void CheckStatus()
        {
            string defines = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone);
            string plugins = Path.Combine(PackageRoot(), "Plugins", "x86_64");
            string[] needed = { "FSR4UnityPlugin.dll", "amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
                                "amd_fidelityfx_loader_vk.dll", "amd_fidelityfx_upscaler_vk.dll" };
            string[] frameGen = { "FSR4FrameGen.dll", "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_framegeneration_vk.dll" };

            var sb = new System.Text.StringBuilder("[FSR4] Status\n");
            sb.AppendLine($"  {Define} defined (Standalone): {HasDefine(defines)}");
            sb.AppendLine($"  Editor graphics API: {SystemInfo.graphicsDeviceType}");
            foreach (var n in needed)
                sb.AppendLine($"  {n}: {(File.Exists(Path.Combine(plugins, n)) ? "present" : "MISSING")}");
            sb.AppendLine("  Optional frame generation (experimental):");
            foreach (var n in frameGen)
                sb.AppendLine($"  {n}: {(File.Exists(Path.Combine(plugins, n)) ? "present" : "missing")}");
            Debug.Log(sb.ToString());
        }

        [MenuItem(Root + "Open native build folder")]
        static void OpenNative()
        {
            string dir = Path.Combine(PackageRoot(), "Native~");
            if (Directory.Exists(dir))
                EditorUtility.RevealInFinder(dir);
            else
                Debug.LogWarning("[FSR4] Native~ folder not found at " + dir);
        }

        static bool HasDefine(string defines)
        {
            foreach (var d in defines.Split(';'))
                if (d.Trim() == Define)
                    return true;
            return false;
        }
    }
}
