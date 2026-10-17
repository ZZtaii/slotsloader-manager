@echo off
setlocal
cd /d "%~dp0.."
if not exist build\obj\core\pak.obj (
    echo Run build.bat first.
    exit /b 1
)
set "VSDIR=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT /Icommon tests\pak_exclusions.cpp build\obj\core\pak.obj build\obj\core\miniz.obj build\obj\core\zstddeclib.obj /Fobuild\obj\tools\pak_exclusions.obj /Febuild\pak_exclusions.exe
exit /b %errorlevel%
