# Building the native plugins

Only needed when `Plugins/x86_64/FSR4UnityPlugin.dll` or `FSR4FrameGen.dll` is missing, stale (Unity logs "out of date ... Rebuild it"), or you change the C++.

## Requirements
- Windows, CMake (`winget install Kitware.CMake`), MSVC C++ toolchain (Visual Studio or Build Tools with *Desktop development with C++*).
- Your Unity Editor folder (the one that contains `Data\PluginAPI`).
- The FidelityFX SDK: `git clone --depth 1 --branch v2.4.0 https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK` (only `Kits/FidelityFX` is used).

## Steps
1. Close the Unity Editor (it locks loaded plugin DLLs).
2. Tell the script where things are - either set environment variables or create `Native~\build.local.bat`:
   ```bat
   set "UNITY_EDITOR_DIR=C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor"
   set "FFX_SDK_DIR=D:\src\FidelityFX-SDK\Kits\FidelityFX"
   ```
   (`FFX_SDK_DIR` is also auto-detected if the SDK sits at `<project>\FidelityFX-SDK-2.4.0`.)
3. Run `Native~\build.bat`. Output goes to `Plugins\x86_64`. Set `FSR4_BUILD_FRAMEGEN=OFF` to skip the frame-generation plugin.
4. Reopen Unity. *Tools > AchiraStudio > FSR4 > Check status* lists every file.

Existing `.meta` files are never overwritten, so GUIDs stay stable.

## AMD DLLs
Copy `amd_fidelityfx_loader_{dx12,vk}.dll`, `amd_fidelityfx_upscaler_{dx12,vk}.dll` and `amd_fidelityfx_framegeneration_{dx12,vk}.dll` from the SDK's `Kits/FidelityFX/signedbin` into `Plugins/x86_64` (they are signed - never modify them).
