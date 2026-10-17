@echo off
setlocal
cd /d "%~dp0.."
set "VSDIR=C:\Program Files\Microsoft Visual Studio\2022\Community"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 exit /b 1
if not exist build\obj\table_test mkdir build\obj\table_test
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /Icostumes\loader /Icommon\third_party /Fobuild\obj\table_test\ tests\costume_table_refresh.cpp costumes\loader\patch.cpp /Febuild\costume_table_refresh.exe
exit /b %errorlevel%
