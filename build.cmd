@echo off
rem Builds "ECR Simulator.exe" (icon + version details embedded) with the .NET Framework 4 compiler that ships with Windows.
rem Created by Ahmad El Lahib.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%~dp0assets\ecr.ico" powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\make-icon.ps1"
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0ECR Simulator.exe" ^
    /win32icon:"%~dp0assets\ecr.ico" /resource:"%~dp0assets\ecr.ico",EcrSim.ecr.ico ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll "%~dp0src\*.cs"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built "%~dp0ECR Simulator.exe"
