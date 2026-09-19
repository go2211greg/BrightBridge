@echo off
rem Regenerates BrightBridge.ico from src\Glyph.cs. Run after changing the mark.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set ROOT=%~dp0..
"%CSC%" -nologo -target:exe -platform:x64 ^
  -out:"%TEMP%\bb-makeicon.exe" ^
  -reference:System.dll -reference:System.Drawing.dll ^
  "%ROOT%\src\Glyph.cs" "%~dp0MakeIcon.cs"
if errorlevel 1 exit /b 1
"%TEMP%\bb-makeicon.exe" "%ROOT%\BrightBridge.ico" %1
del "%TEMP%\bb-makeicon.exe" >nul 2>&1
