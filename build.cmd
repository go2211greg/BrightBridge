@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Cannot find the C# compiler at "%CSC%".
  exit /b 1
)

rem The shell icon is generated from src\Glyph.cs, the same drawing the tray uses.
if not exist "%~dp0BrightBridge.ico" call "%~dp0tools\make-icon.cmd"
set ICON=
if exist "%~dp0BrightBridge.ico" set ICON=-win32icon:"%~dp0BrightBridge.ico"

"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ ^
  -out:"%~dp0BrightBridge.exe" ^
  %ICON% ^
  -reference:System.dll ^
  -reference:System.Drawing.dll ^
  -reference:System.Windows.Forms.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Built "%~dp0BrightBridge.exe"
