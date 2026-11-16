# Changelog

## 0.2.0
- Package split into modules (Core upscaler / Frame generation / Debug HUD / Editor), each its own assembly; modules only meet through `FSR4Hooks` and `FSR4HudSections`.
- New: experimental frame generation for D3D12 and Vulkan (`FSR4FrameGen.dll`), off by default, Player builds only.
- New: Vulkan upscaler backend (experimental), tuning keys, FSR debug view, GPU memory readout.
- Editor menu moved to *Tools > AchiraStudio > FSR4*; package path is resolved through the Package Manager (works for any install location).
- `Native~/build.bat` no longer hard-codes paths: `UNITY_EDITOR_DIR` / `FFX_SDK_DIR` come from the environment or `Native~/build.local.bat`; builds both plugins.
- `FSR4DispatchParams` is 168 bytes: rebuild `FSR4UnityPlugin.dll` after updating from 0.1.x.

## 0.1.0
- First D3D12-only FSR upscaler wrapper.
