# Changelog

## Unreleased
- New: debug HUD module (`Runtime/DebugHud`, assembly `AchiraStudio.FSR4.DebugHud`): upscaler status, rendered frame rate, CPU / GPU frame time, frame-time graph, and the frame-generation block with separate rendered / generated / presented frame rates. Add the *AchiraStudio > FSR4 Debug HUD* component or start the Player with `-fsr4-hud`.
- New: `FSR4FrameRateMeter` measures rendered and generated frames per second separately.
- New: `FSR4FrameGeneration.providerInUse` reports which frame-generation provider is running (native export `FG_GetProviderVersion`).
- New: `FSR4FrameGeneration.sourceCamera` and `FSR4FrameData.camera`.
- Fixed: a second upscaled camera in the same frame made frame generation wait 60 ms per frame and switch to *Stalled* after five frames. One prepare per frame is accepted now, in C# and in the native plugin.
- Fixed: the present hooks read the frame-generation context without holding its mutex, and the context could be destroyed between the generate dispatch and the submission of its command list. The stall counter and the AMD loader are thread-safe now.
- Fixed: `vramBytes` assumed 4 bytes per pixel (wrong for FP16 HDR backbuffers) and ignored the Vulkan textures.
- Fixed: messages pointed at `Native~/README.md`, which does not exist.
- Native: `FSR4FrameGen.dll` and `FSR4UnityPlugin.dll` must be rebuilt (`Native~/build.bat`) for the native fixes and `providerInUse`. No struct size changed, so the DLLs shipped with 0.2.0 keep loading; they just lack the native part of these changes.

## 0.2.0
- Package split into modules (Core upscaler / Frame generation / Debug HUD / Editor), each its own assembly; modules only meet through `FSR4Hooks` and `FSR4HudSections`.
- New: experimental frame generation for D3D12 and Vulkan (`FSR4FrameGen.dll`), off by default, Player builds only.
- New: Vulkan upscaler backend (experimental), tuning keys, FSR debug view, GPU memory readout.
- Editor menu moved to *Tools > AchiraStudio > FSR4*; package path is resolved through the Package Manager (works for any install location).
- `Native~/build.bat` no longer hard-codes paths: `UNITY_EDITOR_DIR` / `FFX_SDK_DIR` come from the environment or `Native~/build.local.bat`; builds both plugins.
- `FSR4DispatchParams` is 168 bytes: rebuild `FSR4UnityPlugin.dll` after updating from 0.1.x.

## 0.1.0
- First D3D12-only FSR upscaler wrapper.
