@echo off
rem ECR Simulator (created by Ahmad El Lahib). Starts the .exe, or the no-exe version when the .exe is missing
rem (some antivirus tools remove unsigned .exe files).
if exist "%~dp0ECR Simulator.exe" (
    start "" "%~dp0ECR Simulator.exe" %*
) else (
    start "" powershell -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Start-EcrSimulator.ps1" %*
)
