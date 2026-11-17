# Unity FSR 4 Wrapper: demo project

Demo, showcase and test project for the [`com.achirastudio.fsr4`](https://github.com/AchiraStudio/unity-fsr4-wrapper) package (`beta` branch). This branch contains only the Unity project; the package itself lives on `beta` and is pulled in through `Packages/manifest.json`.

> **Work in progress.** The test results, screenshots and full documentation are being added to this branch.

## What is in here

| Path | Contents |
|---|---|
| `Assets/FSRShowcase/` | Showcase scene (a copy of Unity's Oasis sample), in-game graphics menu, performance overlay, HUD ghosting test, split-screen rig and the scripted test runner |
| `Assets/Scenes/` | Unity's sample scenes, unmodified. They are Unity's content, not part of this package |
| `Tools/` | `desktop-capture.ps1` (desktop screenshots, needed to see frame-generated frames) and `compose.py` (labelled collages) |
| `ProjectSettings/` | D3D12 only, `ENABLE_UPSCALER_FRAMEWORK` defined, MSAA off |

## Requirements

- Unity 6000.7.0b9 (6.7 beta), URP 17.7. The package itself supports 6000.6 and newer.
- Windows x64, Direct3D 12 for FSR. The Editor runs on Direct3D 11 by default; FSR and frame generation only run in a D3D12 Player build.
- Git on `PATH`, so Unity can fetch the package from its git URL.

## Open it

1. Clone this branch and open the folder with Unity 6000.7.0b9. The first import takes a while (large sample assets and the package's ~150 MB of DLLs).
2. Open `Assets/FSRShowcase/Scenes/FSR_Showcase.unity`.
3. In Play mode press `Esc` for the graphics menu. In the Editor the wrapper reports that it needs Direct3D 12 and the menu falls back to Unity's STP.
4. For real FSR, build with File > Build Profiles (Windows, D3D12) and run the Player.

Hotkeys: `Esc` menu, `F1` performance overlay, `F2` FSR debug HUD, `F3` split-screen, `F4` HUD stress test.

## Licenses

The code in `Assets/FSRShowcase/` and `Tools/` is intended to be MIT, like the package. `Assets/Scenes/`, `Assets/SharedAssets/` and `Assets/Tutorials/` are Unity sample content and are not covered by that license; check Unity's terms for the sample project before redistributing them.
