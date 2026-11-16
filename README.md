# Unity FSR 4 Wrapper

**AMD FidelityFX Super Resolution 4 as a drop-in upscaler for Unity 6 and URP, with an optional frame-generation module.**

[![Unity](https://img.shields.io/badge/Unity-6000.6%2B-black?logo=unity)](https://unity.com)
[![URP](https://img.shields.io/badge/URP-17.6.0-blue)](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.6/manual/index.html)
[![FidelityFX SDK](https://img.shields.io/badge/FidelityFX%20SDK-2.4.0-ed1c24)](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE.md)

This package plugs FSR into Unity's **Upscaler Framework** as a custom upscaler. FSR 4 (ML) runs on GPUs and drivers that
support it, and the runtime falls back to FSR 3.1 everywhere else. It runs on **Direct3D 12** (main path) and **Vulkan** (experimental).

> [!WARNING]
> **Status: pre-release, not yet validated on hardware.** The native plugins compile and link against the real FidelityFX and
> Unity 6.6 headers, and the C# follows the pattern of Unity's own FSR2 wrapper. Expect a bring-up session on your machine.
> Frame generation is the least-tested part: it is off by default and only works in Player builds.
> Please [open an issue](../../issues) with your GPU, driver and log output if something misbehaves.

## Features

- **FSR 4 (ML) upscaling** with automatic fallback to FSR 3.1 on unsupported hardware.
- **Native Unity integration** through the Upscaler Framework: pick it from the URP asset, no custom render passes to wire up.
- **Direct3D 12 and Vulkan** backends.
- **Modular by design:** upscaler, frame generation and editor tooling are separate assemblies that only meet through public hooks, so you can delete any module without breaking the others.
- **Tuning and diagnostics:** FSR debug view, tuning keys, GPU memory readout, and a startup check that detects stale native plugins.
- **Experimental frame generation** (D3D12 and Vulkan) via FidelityFX interpolation.

## Requirements

| | |
|---|---|
| Unity | 6000.6 or newer |
| Render pipeline | Universal RP 17.6.0 (installed automatically as a dependency) |
| Platform | Windows x64, Standalone and Editor |
| Graphics API | Direct3D 12 (recommended) or Vulkan (experimental) |
| GPU | Any DirectX 12 GPU. FSR 4 (ML) needs a supported Radeon GPU and driver, others use FSR 3.1. Frame generation 4.x needs a Radeon RX 9000 series card, others get FG 3.1. |

## Installation

### Option 1: Package Manager, git URL (recommended)

1. In Unity open **Window > Package Manager**.
2. Click **+ > Add package from git URL...**
3. Paste:

   ```
   https://github.com/AchiraStudio/unity-fsr4-wrapper.git
   ```

4. Click **Add**.

To pin a version, append a tag, for example `...unity-fsr4-wrapper.git#v0.2.0`.

Or add it straight to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.achirastudio.fsr4": "https://github.com/AchiraStudio/unity-fsr4-wrapper.git"
  }
}
```

> [!NOTE]
> The package bundles AMD's signed FidelityFX DLLs (about 150 MB), so the first import takes a moment. Git must be installed and on your `PATH` for Unity to resolve git URLs.

### Option 2: Local copy

1. Clone or download this repository.
2. Copy the folder into your project's `Packages/` directory as `Packages/com.achirastudio.fsr4`,
   or use **Package Manager > + > Add package from disk...** and select `package.json`.

## Setup

After installing, enable the upscaler in four steps:

1. Open **Tools > AchiraStudio > FSR4**.
2. Run **1. Enable Upscaler Framework define**. This adds `ENABLE_UPSCALER_FRAMEWORK` for Windows Standalone.
3. Run **2. Use Direct3D12 only** (or **2b. Use Vulkan only** to test Vulkan), then **restart the Editor**.
4. Select your URP asset, set the upscaler to **AMD FSR 4 (Custom Wrapper)** and turn **MSAA off**.

Run **Tools > AchiraStudio > FSR4 > Check status** at any time. It lists every plugin DLL and whether it is present and current.

In the Console you should see an `[FSR4] Providers:` line telling you which FSR version and backend were selected.

## Frame generation (experimental)

Frame generation is **off by default**, works in **Player builds only**, and ships in its own module. Enable it with any of:

```csharp
AchiraStudio.FSR4.FrameGeneration.FSR4FrameGeneration.enabled = true;
```

- add the **AchiraStudio > FSR4 Frame Generation Controller** component to an object, or
- launch the game with `-fsr4-framegen`.

A minimal toggle is available under **Package Manager > AchiraStudio FSR > Samples > Frame generation toggle**.
For how it works, requirements, latency and diagnostics, read [Frame generation](Documentation~/Frame-generation.md).

## Modules

Every module is its own assembly. Delete the ones you do not need.

| Module | Assembly | Folder | Remove by deleting |
|---|---|---|---|
| **Core upscaler** (required) | `AchiraStudio.FSR4.Runtime` | `Runtime/Core` | n/a |
| **Frame generation** | `AchiraStudio.FSR4.FrameGeneration` | `Runtime/FrameGeneration` | the folder, `FSR4FrameGen.dll` and `amd_fidelityfx_framegeneration_*.dll` |
| **Editor menu** | `AchiraStudio.FSR4.Editor` | `Editor` | the folder |

All runtime assemblies compile only when the `ENABLE_UPSCALER_FRAMEWORK` define is set.

## How it works

```
URP post-processing -> Unity Upscaler Framework (UpscalerRegistry / IUpscaler / UpscalingIO)
   -> FSR4Upscaler (C#)  render-graph unsafe pass: output texture + parameter block
        -> IssuePluginEventAndData -> FSR4UnityPlugin.dll (render thread)
             -> amd_fidelityfx_loader_{dx12,vk}.dll -> amd_fidelityfx_upscaler_{dx12,vk}.dll (AMD, signed)
```

Jitter, quality ratios, mip bias and resolution live in C#. The native plugin owns the D3D12 and Vulkan objects. On D3D12 the work is recorded into the
plugin's own command list with explicit resource states. On Vulkan it is recorded into Unity's command buffer.

## Troubleshooting

| Symptom | Try |
|---|---|
| Nothing happens | Read the Console warning from the `FSR4Upscaler` constructor. It names the cause (wrong graphics API, missing DLL, failed probe). |
| Shimmer or a doubled image while the camera is still | Enable **Invert Jitter** in the options. |
| Ghosting trails | Check motion vector sign and scale in `RecordRenderGraph`, enable **Debug Checking** and read `FSR4Native.LastMessage()`. |
| "out of date ... Rebuild it" in the log | The native plugin does not match the C# structs. See [Building the native plugins](Documentation~/Building-the-native-plugins.md). |
| FSR 4 (ML) unavailable on Vulkan | Unity does not enable the needed device extensions on Vulkan. Check the `[FSR4] Providers:` log line, or use D3D12. |
| Cannot rebuild the DLL | Close Unity. A loaded native plugin stays locked. |

Not wired up: reactive and transparency masks and the exposure texture (URP produces none, so auto exposure is on), and the Denoiser and Radiance Cache (they need ray tracing).

## Building the native plugins

The repository ships prebuilt `FSR4UnityPlugin.dll` and `FSR4FrameGen.dll`. You only need to rebuild them if you change the C++ or the
startup check reports them as stale. See [Building the native plugins](Documentation~/Building-the-native-plugins.md).

## Repository layout

```
.
├── package.json            UPM manifest
├── Runtime/Core            upscaler
├── Runtime/FrameGeneration experimental frame generation
├── Editor/                 Tools > AchiraStudio > FSR4 menu
├── Plugins/x86_64/         native plugins and AMD's signed DLLs
├── Native~/                C++ sources and CMake build (not imported by Unity)
├── Documentation~/         guides
└── Samples~/               importable samples
```

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## License

The code and documentation in this repository are released under the [MIT License](LICENSE.md).
Bundled third-party components keep their own licenses, see [Third Party Notices](Third%20Party%20Notices.md).

This project is not affiliated with or endorsed by AMD or Unity. FidelityFX and FSR are trademarks of Advanced Micro Devices, Inc.
