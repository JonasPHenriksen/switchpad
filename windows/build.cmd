@echo off
rem Builds SwitchPadBridge.exe with the C# compiler included in Windows (.NET Framework 4).
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /optimize /platform:anycpu /out:"%~dp0SwitchPadBridge.exe" "%~dp0SwitchPadBridge.cs"
if errorlevel 1 (echo Build FAILED & exit /b 1)
echo Built "%~dp0SwitchPadBridge.exe"
