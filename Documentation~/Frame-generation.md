# Frame generation (experimental)

AMD FidelityFX frame generation (interpolation). **Off by default. Player builds only. Run on one GPU (Radeon RX 9060 XT, Direct3D 12); see the README's *Validation and test results* for the measurements.**

## Why it needs its own plugin and a Player build
FidelityFX normally replaces the engine's swapchain. Unity's plugin API cannot do that, so `FSR4FrameGen.dll` uses the SDK's
manual mode (`FFX_FRAMEGENERATION_FLAG_NO_SWAPCHAIN_CONTEXT_NOTIFY`) and drives presentation itself:

1. After the upscale pass the module records FFX *PrepareV2* (depth, motion vectors, jitter, camera vectors) into Unity's command stream.
2. It hooks the swapchain present: D3D12 patches `IDXGISwapChain::Present/Present1` in the vtable of the swapchain from `IUnityGraphicsD3D12v8::GetSwapChain()`; Vulkan intercepts `vkCreateSwapchainKHR`, `vkDestroySwapchainKHR` and `vkQueuePresentKHR` through `InterceptVulkanAPI`.
3. When Unity presents frame N the plugin copies the backbuffer, runs `ffxDispatch(FrameGeneration)` (-> interpolated frame between N-1 and N), presents the interpolated frame, puts frame N into the next backbuffer and presents it half a frame interval later.

The Editor has no swapchain for D3D12 (`GetSwapChain()` returns null) and loads the plugin too late for the Vulkan hook, so there the module only reports a status.

## Enabling
- Code: `AchiraStudio.FSR4.FrameGeneration.FSR4FrameGeneration.enabled = true;`
- Component: add *AchiraStudio > FSR4 Frame Generation Controller* to an object.
- Command line: `-fsr4-framegen`.

## Requirements and caveats
- FG 4.x needs a Radeon RX 9000 series GPU; other GPUs get the FG 3.1 provider. FG expects roughly 30+ real fps.
- Adds about one frame of latency (the real frame is held back for the interpolated one).
- The UI is part of the backbuffer, so animated UI can ghost. The optional HUD-less input (below) reduces that on D3D12.
- Split-screen: frame generation pauses while two or more base cameras that render to the screen each cover at least 20% of it (`pauseInSplitScreen`, default on). The check runs in `RenderPipelineManager.beginContextRendering`, before any camera renders, so the first camera of a split-screen frame is already skipped; no prepare is recorded, the present hook sees nothing new and passes the frame through. When one view is left, the next prepare carries a history reset. Overlay cameras, render-to-texture cameras and views smaller than 20% of the screen are not counted. Setting `sourceCamera` or `pauseInSplitScreen = false` bypasses the pause.
- One camera drives frame generation per frame, because the swapchain is presented once per frame and the whole backbuffer is interpolated from one camera's depth and motion vectors. By default that is the first camera upscaled in the frame; further upscaled cameras in the same frame are ignored (a warning is logged once). Set `FSR4FrameGeneration.sourceCamera` to choose the camera. Split-screen is not supported.
- Vulkan: the plugin must be preloaded in the Player (the `.meta` does this) so the swapchain is created through the hook; the hook adds one spare swapchain image and `TRANSFER_SRC/DST` usage.
- D3D12 backbuffer formats supported: R8G8B8A8, B8G8R8A8, R10G10B10A2, R16G16B16A16_FLOAT.
- If the present hook does not keep up for 5 consecutive frames the plugin switches itself off (state *Stalled*).

## HUD-less input (experimental, D3D12, SDR)
FidelityFX can separate UI from the scene when it is given the frame without the UI next to the backbuffer. The `FSR4HudlessFeature` URP renderer feature (*Add Renderer Feature > FSR4 HUD-less Capture*) provides it:

1. A render-graph pass at `RenderPassEvent.AfterRenderingPostProcessing` copies the camera colour into a persistent texture. That is after URP's post processing and before URP's final blit, and URP draws its screen-space overlay UI only after the final blit (`UniversalRendererRenderGraph`), so the copy has no overlay UI.
2. An unsafe pass then issues the plugin event `kEventHudless` (3) with the texture. The event handler (render thread) checks the texture matches the backbuffer size, re-configures the frame-generation context for the frame that was just prepared with `HUDLessColor` set, and executes an empty command list whose only purpose is to tell Unity which state the texture must be in (`SRV`) when the generate dispatch reads it at present time.
3. If the texture format differs from the backbuffer format, the context is re-created once with `ffxCreateContextDescFrameGenerationHudless`, so the first frame with a new format goes without. If the provider refuses that description, HUD-less input switches itself off and frame generation carries on.

Enable it with `FSR4FrameGeneration.hudless = true`, the controller component, the debug HUD toggle or `-fsr4-hudless`. `FSR4FrameGeneration.hudlessStatus` says whether it works and what is missing; `hudlessFrames` counts the frames that got a HUD-less image.

Limits to know about:
- The copy has to equal the backbuffer pixel for pixel apart from the UI. URP's final post-processing step (FXAA, dithering, film grain) runs after the capture, so disable FXAA and dithering on the camera; anything that differs is read as UI. The feature warns once when FXAA or dithering is on.
- Only UI that is drawn after the final blit can be separated: Screen Space - Overlay canvases and UI Toolkit drawn by URP. UI inside the scene (Screen Space - Camera, World Space) is part of the scene colour.
- Not implemented for Vulkan or HDR output (in HDR, URP renders the UI into its own texture and composites it).
- With several cameras, the capture is taken from the camera that was prepared for frame generation (see `sourceCamera`) and camera stacking is not supported.
- It needs an `FSR4FrameGen.dll` built from these sources. An older DLL does not export `FG_GetHudlessFrames` and ignores the event; `hudlessStatus` then asks for a rebuild.
- The hand-over adds a command-buffer flush per frame (the event is configured like the prepare event).
- Run on a Radeon RX 9060 XT: it reports `Active` and gives a small reduction in artifacts around fast-moving HUD elements (3 to 4 %, within the capture-to-capture spread). Not run on other hardware.

## Diagnostics report and benchmark
`FSR4DiagnosticsReport.Build()` returns a plain-text report (system, upscaler, frame generation) for bug reports; the debug HUD has **Copy report**, **Save report** and **Run benchmark** buttons. See the README's *Performance monitor* section for the benchmark output.

## Status API
`FSR4FrameGeneration.state`, `.statusMessage`, `.GetStats()` (prepared / generated / passed-through frames, smoothed base frame time, GPU memory, last FFX error code).

`FSR4FrameGeneration.providerInUse` is the version name of the frame-generation provider FidelityFX actually selected (FG 4.x or 3.1). It is empty until generation has started. It needs an `FSR4FrameGen.dll` built from the current sources; with an older DLL it stays empty.

`FSR4FrameRateMeter` measures the rendered and the generated frame rate separately (see the README), and the debug HUD module (`FSR4DebugHud`, or `-fsr4-hud` on the command line) shows both together with the state and the counters.

## Diagnostics

While frame generation is active the native plugin writes `FSR4FrameGen.log` next to `FSR4FrameGen.dll` (in a Player: `<Game>_Data/Plugins/x86_64/`). It records the hook installation (which module owns the original `Present`) and the first generated frames stage by stage, so a crash inside a hook can be traced. If a generated frame faults inside `Present`, frame generation switches itself off for the rest of the session instead of crashing the game.

### Diagnostic modes (D3D12)

If frame generation removes the D3D12 device, create a text file `FSR4FrameGen.mode` next to `FSR4FrameGen.dll` (in a Player build: `<Game>_Data/Plugins/x86_64/`) containing one digit, then restart the game and send `FSR4FrameGen.log`:

| Mode | What it does |
| --- | --- |
| 0 | Full frame generation (default, no file needed). |
| 1 | Generates the frame and writes it into the backbuffer, but only presents once. Tests the FFX output and the copy. |
| 2 | Presents the real frame untouched, then the generated frame (two presents; FFX never touches Unity's buffer). |
| 3 | No FFX dispatch, just presents the same frame twice. Tests the double Present on its own. |
| 4 | Like 0 but without the index-preserving presents (old behaviour; removes the device on frame 2). |

`FSR4FrameGen.mode.txt` is accepted too (Explorer adds `.txt` when extensions are hidden).

**Backbuffer index:** Unity tracks the swapchain's backbuffer index itself and expects exactly one Present per frame. After the interpolated + real presents, the plugin presents the real frame `BufferCount - 1` more times so DXGI's index ends up where Unity expects. Without this, Unity renders into a non-current backbuffer on the next frame and the device is removed with `DXGI_ERROR_ACCESS_DENIED` (0x887A002B). Because every present waits for a vblank with VSync on, use frame generation with VSync off.

The log shows `diagnostic mode = N`, the device-removed reason at the start of each generated frame, and the result of each Present.

### Performance

The log line `perf (240 frames): real N fps ...` is written every 240 generated frames. It shows the real (rendered) frame rate, the average time spent in the present hook, the average pacing wait and the average wait for earlier command lists. With frame generation the display receives about twice the real frame rate; the game's FPS counter shows both. The pacing wait (`pacing` option) blocks the render thread for half a frame interval: if the render thread is the bottleneck, turn `pacing` off to keep the real frame rate.

Be aware of what that trades away. With `pacing` off the real frame is presented immediately after the generated one, so the generated frame is on screen only for the time the copy and the second present take. The `generated` counter keeps counting, but the picture may not look any smoother. The counters report frames whose present call succeeded, not frames the display showed.
