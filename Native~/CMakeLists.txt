cmake_minimum_required(VERSION 3.20)
project(FSR4Native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# <FidelityFX-SDK>/Kits/FidelityFX  (the folder that contains api/, upscalers/ and framegeneration/)
set(FFX_SDK_DIR "" CACHE PATH "FidelityFX SDK Kits/FidelityFX directory")
# <Unity install>/Editor/Data/PluginAPI  (IUnityInterface.h, IUnityGraphics.h, IUnityGraphicsD3D12.h, IUnityGraphicsVulkan.h)
set(UNITY_PLUGIN_API_DIR "" CACHE PATH "Unity PluginAPI header directory")
option(FSR4_BUILD_UPSCALER  "Build FSR4UnityPlugin.dll (upscaler)"                 ON)
option(FSR4_BUILD_FRAMEGEN  "Build FSR4FrameGen.dll (experimental frame generation)" ON)

if(NOT EXISTS "${FFX_SDK_DIR}/api/include/ffx_api.h")
    message(FATAL_ERROR "FFX_SDK_DIR must point at the SDK's Kits/FidelityFX folder (api/include/ffx_api.h not found).")
endif()
if(NOT EXISTS "${UNITY_PLUGIN_API_DIR}/IUnityGraphicsVulkan.h")
    message(FATAL_ERROR "UNITY_PLUGIN_API_DIR must be <Unity>/Editor/Data/PluginAPI (IUnityGraphicsVulkan.h not found).")
endif()

set(PLUGIN_OUT "${CMAKE_CURRENT_LIST_DIR}/../Plugins/x86_64")

# Shared setup. third_party/vulkan: vendored Khronos Vulkan headers (Unity's IUnityGraphicsVulkan.h includes <vulkan/vulkan.h>).
function(fsr4_add_plugin target source meta_template)
    add_library(${target} SHARED ${source})
    target_include_directories(${target} PRIVATE "${FFX_SDK_DIR}" "${UNITY_PLUGIN_API_DIR}" "${CMAKE_CURRENT_LIST_DIR}/third_party/vulkan")
    set_target_properties(${target} PROPERTIES PREFIX "" OUTPUT_NAME "${target}")
    if(MSVC)
        target_compile_options(${target} PRIVATE /W3 /permissive- /wd4068)
    endif()
    # Drop the DLL (and its importer settings) next to the signed AMD DLLs. An existing .meta is never overwritten
    # (it carries the GUID other assets may reference).
    add_custom_command(TARGET ${target} POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${PLUGIN_OUT}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<TARGET_FILE:${target}>" "${PLUGIN_OUT}/${target}.dll"
        COMMAND ${CMAKE_COMMAND} -DSRC=${CMAKE_CURRENT_LIST_DIR}/${meta_template} -DDST=${PLUGIN_OUT}/${target}.dll.meta
                -P "${CMAKE_CURRENT_LIST_DIR}/copy_if_missing.cmake"
    )
endfunction()

if(FSR4_BUILD_UPSCALER)
    fsr4_add_plugin(FSR4UnityPlugin src/fsr4_plugin.cpp FSR4UnityPlugin.dll.meta.template)
endif()
if(FSR4_BUILD_FRAMEGEN)
    fsr4_add_plugin(FSR4FrameGen src/fsr4_framegen.cpp FSR4FrameGen.dll.meta.template)
endif()
