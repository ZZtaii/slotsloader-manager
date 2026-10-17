@echo off
setlocal
cd /d "%~dp0.."
if not exist build\obj\core\structural.obj (
    echo Run build.bat first.
    exit /b 1
)
set "VSDIR=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1
set O=build\obj\core
cl /nologo /std:c++17 /EHsc /O2 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /Icommon /Icommon\third_party /Icostumes\loader tests\game_derived.cpp %O%\pak.obj %O%\miniz.obj %O%\zstddeclib.obj %O%\patch.obj %O%\structural.obj %O%\structural_scene.obj %O%\structural_msg.obj %O%\vanilla_inventory.obj %O%\fighter_names.obj /Fobuild\obj\tools\game_derived.obj /Febuild\game_derived.exe
exit /b %errorlevel%
