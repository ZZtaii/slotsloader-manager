@echo off
setlocal
cd /d "%~dp0.."
set "VSDIR=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1
if not exist build\obj\swap_test mkdir build\obj\swap_test
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /Icostumes\loader /Icommon\third_party /Fobuild\obj\swap_test\ tests\swap_repairs.cpp costumes\loader\patch.cpp /Febuild\swap_repair_test.exe /link bcrypt.lib
exit /b %errorlevel%
