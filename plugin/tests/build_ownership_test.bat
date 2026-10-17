@echo off
setlocal
cd /d "%~dp0"

rem Builds and runs ownership_test.exe (unit test of plugin\src\ownership.hpp) next to this file.
set "VSDIR=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1

if not exist obj mkdir obj
cl /nologo /std:c++20 /O2 /MT /EHsc /W4 /WX /Fo"obj\\" /Fe"ownership_test.exe" ownership_test.cpp
if errorlevel 1 exit /b 1
rem marker_test.exe: the private opt-out marker (plugin\src\marker.hpp); its name comes from the
rem environment variable SLOTS_OPTOUT_NAME, which this repository does not hold
cl /nologo /std:c++20 /O2 /MT /EHsc /W4 /WX /Fo"obj\\" /Fe"marker_test.exe" marker_test.cpp
if errorlevel 1 exit /b 1
.\ownership_test.exe
if errorlevel 1 exit /b 1
.\marker_test.exe
