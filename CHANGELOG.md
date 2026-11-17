# Changelog

## Unreleased
- Fixed: compile error on Unity 6.7 (URP 17.7): `'FSR4Upscaler' does not implement inherited abstract member 'AbstractUpscaler.upscalerId'`. URP 17.7 registers upscalers by a stable id plus a display name and requires `upscalerId`. The package now registers as `achirastudio.fsr4` there. The switch is a `versionDefines` entry on `AchiraStudio.FSR4.Runtime` (`FSR4_UPSCALER_ID_API`, set for `com.unity.render-pipelines.core` 17.7.0 and newer), so Unity 6.6 (URP 17.6) keeps the original name-only registration unchanged. Compiled on Unity 6000.7.0b9; the 6.6 path is unchanged code but has not been recompiled on 6.6 since this change.
- Fixed (6.7 and newer): `FSR4Upscaler.isSupportedOnDevice` now reports whether the native plugin started. URP 17.7 picks the first supported upscaler from the asset's priority list, so a machine where the plugin cannot start (for example the Editor on Direct3D 11) falls back to the next entry instead of staying on a non-working upscaler.
- Known: Unity 6.7 reports `UAL0010` / `UAL0013` warnings for static state in this package (static-state lifecycle analyzer). They do not affect behavior.
- New: split-screen support. Upscaling already runs per camera in URP (one context and temporal history per view, sized to the viewport); the per-view state is now visible: `FSR4Stats.GetActiveViews`, a per-view list in the debug HUD and in the diagnostics report. Frame generation pauses while two or more base cameras share the screen (`FSR4FrameGeneration.pauseInSplitScreen`, `isSplitScreenDetected`, `screenViewCount`, `pausedForSplitScreen`, `splitScreenPausedFrames`; also a toggle on the Frame Generation Controller), because it interpolates the whole backbuffer from one camera's data. It resumes with a history reset. Interpolating a split-screen from composed inputs is not implemented.
- Fixed (native): the Direct3D 12 upscaler's ring of command lists had 6 slots, which made a 4-view split-screen wait for the previous frame's GPU work. It has 16 now. Rebuild `FSR4UnityPlugin.dll`.
- New (experimental): HUD-less input for frame generation, to reduce UI ghosting. The `FSR4HudlessFeature` URP renderer feature captures the frame before the overlay UI is drawn and hands it to FidelityFX (`HUDLessColor`) through a new `kEventHudless` plugin event. Direct3D 12 and SDR output only. Enable with `FSR4FrameGeneration.hudless`, the controller component, the debug HUD or `-fsr4-hudless`; `hudlessStatus` and `hudlessFrames` report what happens. Needs a rebuilt `FSR4FrameGen.dll` (an older DLL ignores the event and the status says so). Not run on hardware. The `AchiraStudio.FSR4.FrameGeneration` assembly now references the URP assemblies.
- New: `FSR4DiagnosticsReport.Build()` (plain-text report for bug reports, extendable with `FSR4ReportSources`), 1% and 0.1% lows in the debug HUD, **Copy report** / **Save report** / **Run benchmark** buttons, and `FSR4Benchmark` (warm-up, fixed duration, CSV and JSON output, `-fsr4-benchmark` command line).
- New: `FSR4FrameStatistics.Summarize` (average, median, worst, 1% and 0.1% lows).
- Fixed: `FSR4FrameGenerationController.OnDisable` switched frame generation off even when another controller (or your own code) was driving it. Only the controller that last applied its settings turns it off now.
- Not done on purpose: reactive / transparency mask inputs (FSR 4 ignores them, only the FSR 3.1 fallback would use them) and a rewrite of the Direct3D 12 extra-present path (risk of device removal without hardware to test on).
- New: debug HUD module (`Runtime/DebugHud`, assembly `AchiraStudio.FSR4.DebugHud`): upscaler status, rendered frame rate, CPU / GPU frame time, frame-time graph, and the frame-generation block with separate rendered / generated / presented frame rates. Add the *AchiraStudio > FSR4 Debug HUD* component or start the Player with `-fsr4-hud`.
- New: `FSR4FrameRateMeter` measures rendered and generated frames per second separately.
- New: `FSR4FrameGeneration.providerInUse` reports which frame-generation provider is running (native export `FG_GetProviderVersion`).
- New: `FSR4FrameGeneration.sourceCamera` and `FSR4FrameData.camera`.
- Fixed: a second upscaled camera in the same frame made frame generation wait 60 ms per frame and switch to *Stalled* after five frames. One prepare per frame is accepted now, in C# and in the native plugin.
- Fixed: the present hooks read the frame-generation context without holding its mutex, and the context could be destroyed between the generate dispatch and the submission of its command list. The stall counter and the AMD loader are thread-safe now.
- Fixed: `vramBytes` assumed 4 bytes per pixel (wrong for FP16 HDR backbuffers) and ignored the Vulkan textures.
- Fixed: messages pointed at `Native~/README.md`, which does not exist.
- Fixed: the debug HUD, and its frame-generation block, rebuilt their text on every IMGUI event and allocated garbage every frame. They refresh four times per second now.
- Fixed: on Direct3D 12 the diagnostic `GetCurrentBackBufferIndex` hook was installed on every swapchain, although it only logs. It is now installed only when an `FSR4FrameGen.mode` file selects a diagnostic mode other than 0. (Native change: rebuild `FSR4FrameGen.dll`.)
- Native: `FSR4FrameGen.dll` and `FSR4UnityPlugin.dll` must be rebuilt (`Native~/build.bat`) for the native fixes, `providerInUse`, the HUD-less input and the command-list ring. No struct size changed, so the DLLs shipped with 0.2.0 keep loading; they just lack the native part of these changes.

## 0.2.0
- Package split into modules (Core upscaler / Frame generation / Debug HUD / Editor), each its own assembly; modules only meet through `FSR4Hooks` and `FSR4HudSections`.
- New: experimental frame generation for D3D12 and Vulkan (`FSR4FrameGen.dll`), off by default, Player builds only.
- New: Vulkan upscaler backend (experimental), tuning keys, FSR debug view, GPU memory readout.
- Editor menu moved to *Tools > AchiraStudio > FSR4*; package path is resolved through the Package Manager (works for any install location).
- `Native~/build.bat` no longer hard-codes paths: `UNITY_EDITOR_DIR` / `FFX_SDK_DIR` come from the environment or `Native~/build.local.bat`; builds both plugins.
- `FSR4DispatchParams` is 168 bytes: rebuild `FSR4UnityPlugin.dll` after updating from 0.1.x.

## 0.1.0
- First D3D12-only FSR upscaler wrapper.
