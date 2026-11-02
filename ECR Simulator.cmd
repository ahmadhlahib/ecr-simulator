@echo off
rem ECR Simulator (created by Ahmad El Lahib). Opens the window; no .exe needed.
start "" powershell -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Start-EcrSimulator.ps1" %*
