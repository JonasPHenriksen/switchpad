@echo off
rem Builds SwitchPad.exe (window + tray) and SwitchPadBridge.exe (console) with the C# compiler
rem included in Windows (.NET Framework 4). Nothing to install.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "SRC="%~dp0SwitchPadBridge.cs" "%~dp0SwitchPadGui.cs""
set "REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll"
"%CSC%" /nologo /optimize /platform:anycpu %REFS% /target:winexe /define:GUI /out:"%~dp0SwitchPad.exe" %SRC%
if errorlevel 1 (echo Build of SwitchPad.exe FAILED & exit /b 1)
"%CSC%" /nologo /optimize /platform:anycpu %REFS% /out:"%~dp0SwitchPadBridge.exe" %SRC%
if errorlevel 1 (echo Build of SwitchPadBridge.exe FAILED & exit /b 1)
echo Built "%~dp0SwitchPad.exe" (window) and "%~dp0SwitchPadBridge.exe" (console)
