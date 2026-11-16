@echo off
setlocal

rem Builds the package's native plugins (FSR4UnityPlugin.dll, FSR4FrameGen.dll) with the MSVC x64 toolchain and copies
rem them into ..\Plugins\x86_64. Works with Visual Studio OR just "Build Tools for Visual Studio" (Desktop development
rem with C++). Close the Unity Editor first - it keeps the DLLs locked once a plugin has been loaded.
rem
rem Configuration (environment variables, or a build.local.bat next to this file that sets them - it is not part of
rem the package and is safe to keep per project/machine):
rem   UNITY_EDITOR_DIR = the Editor folder that contains Data\PluginAPI   (e.g. C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor)
rem   FFX_SDK_DIR      = ...\FidelityFX-SDK-2.4.0\Kits\FidelityFX
rem   FSR4_BUILD_UPSCALER / FSR4_BUILD_FRAMEGEN = ON or OFF (default ON for both)

if exist "%~dp0build.local.bat" call "%~dp0build.local.bat"

if "%FFX_SDK_DIR%"=="" (
    rem Look for an SDK clone/download next to the package or its project.
    for %%D in ("%~dp0..\..\..\FidelityFX-SDK-2.4.0\Kits\FidelityFX" "%~dp0..\..\FidelityFX-SDK-2.4.0\Kits\FidelityFX" "%~dp0FidelityFX-SDK\Kits\FidelityFX") do (
        if exist "%%~D\api\include\ffx_api.h" set "FFX_SDK_DIR=%%~D"
    )
)
if "%FSR4_BUILD_UPSCALER%"=="" set "FSR4_BUILD_UPSCALER=ON"
if "%FSR4_BUILD_FRAMEGEN%"=="" set "FSR4_BUILD_FRAMEGEN=ON"

if "%UNITY_EDITOR_DIR%"=="" (
    echo UNITY_EDITOR_DIR is not set. Set it to your Unity Editor folder ^(the one containing Data\PluginAPI^), e.g.
    echo     set "UNITY_EDITOR_DIR=C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor"
    echo or put that line into "%~dp0build.local.bat".
    exit /b 1
)
if not exist "%UNITY_EDITOR_DIR%\Data\PluginAPI\IUnityGraphicsVulkan.h" (
    echo Could not find Unity PluginAPI headers under "%UNITY_EDITOR_DIR%\Data\PluginAPI".
    exit /b 1
)
if "%FFX_SDK_DIR%"=="" (
    echo FFX_SDK_DIR is not set and no FidelityFX SDK was found next to the package.
    echo Get it with:  git clone --depth 1 --branch v2.4.0 https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK
    echo then set FFX_SDK_DIR to ...\FidelityFX-SDK\Kits\FidelityFX ^(or put it in build.local.bat^).
    exit /b 1
)
if not exist "%FFX_SDK_DIR%\api\include\ffx_api.h" (
    echo Could not find the FidelityFX SDK at "%FFX_SDK_DIR%".
    exit /b 1
)

where cmake >nul 2>nul
if errorlevel 1 (
    echo cmake not found on PATH. Install it with:  winget install Kitware.CMake
    echo Then open a NEW terminal and run this again.
    exit /b 1
)

rem Find the MSVC environment (vswhere ships with VS and with Build Tools).
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo vswhere not found - Visual Studio Build Tools do not look installed.
    echo Install:  winget install Microsoft.VisualStudio.2022.BuildTools --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
    exit /b 1
)
set "VSINSTALL="
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if "%VSINSTALL%"=="" (
    echo No MSVC C++ toolset found. Add the "Desktop development with C++" workload in the Visual Studio Installer.
    exit /b 1
)
call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

if exist "%~dp0build" rmdir /s /q "%~dp0build"
cmake -S "%~dp0." -B "%~dp0build" -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release ^
    -DFFX_SDK_DIR="%FFX_SDK_DIR%" -DUNITY_PLUGIN_API_DIR="%UNITY_EDITOR_DIR%\Data\PluginAPI" ^
    -DFSR4_BUILD_UPSCALER=%FSR4_BUILD_UPSCALER% -DFSR4_BUILD_FRAMEGEN=%FSR4_BUILD_FRAMEGEN%
if errorlevel 1 exit /b 1

cmake --build "%~dp0build"
if errorlevel 1 exit /b 1

echo.
echo Built. The DLLs are in %~dp0..\Plugins\x86_64
endlocal
