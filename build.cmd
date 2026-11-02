@echo off
rem Optional: builds "ECR Simulator.exe" with the .NET Framework 4 compiler that ships with Windows.
rem The company antivirus may delete the .exe; "ECR Simulator.cmd" works without it.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0ECR Simulator.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0src\*.cs"
