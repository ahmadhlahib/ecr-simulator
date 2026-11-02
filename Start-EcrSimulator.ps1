# ECR Simulator launcher - created by Ahmad El Lahib.
# Compiles src\*.cs in memory and opens the window, so no .exe lands on disk (the antivirus deletes our .exe builds).
# Usage: powershell -STA -ExecutionPolicy Bypass -File Start-EcrSimulator.ps1 [-Port COM21] [-Snapshot shot.png]
param(
    [string]$Port,
    [string]$Snapshot
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sources = @(Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName })
if (-not ('EcrSim.Program' -as [type])) {
    Add-Type -Path $sources -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Core
}
$argsList = New-Object System.Collections.Generic.List[string]
if ($Port) { $argsList.Add('--port'); $argsList.Add($Port) }
if ($Snapshot) { $argsList.Add('--snapshot'); $argsList.Add($Snapshot) }
[EcrSim.Program]::Run($here, $argsList.ToArray())
