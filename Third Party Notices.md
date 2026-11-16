# Third party notices

- **AMD FidelityFX SDK 2.4.0** - headers are used to build the native plugins and the signed binaries
  `amd_fidelityfx_loader_*.dll`, `amd_fidelityfx_upscaler_*.dll`, `amd_fidelityfx_framegeneration_*.dll` are redistributed unmodified.
  Copyright (c) Advanced Micro Devices, Inc. MIT License. https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK
- **Khronos Vulkan-Headers** - vendored under `Native~/third_party/vulkan` (needed because Unity's `IUnityGraphicsVulkan.h` includes
  `<vulkan/vulkan.h>`). See the license files in that folder. https://github.com/KhronosGroup/Vulkan-Headers
- **Unity native plugin API headers** (`IUnityInterface.h`, ...) are *not* redistributed; the build reads them from your Unity installation (Unity Companion License).
