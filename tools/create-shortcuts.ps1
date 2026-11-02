# Creates "ECR Simulator" shortcuts (Desktop + Start menu) with the app icon. Created by Ahmad El Lahib.
# The shortcut opens the .exe when it is there, otherwise the no-exe launcher (same window, no console).
param([string[]]$Folders)
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'ECR Simulator.exe'
$icon = Join-Path $root 'assets\ecr.ico'
$shell = New-Object -ComObject WScript.Shell
$targets = if ($Folders) { $Folders } else { @(
    [Environment]::GetFolderPath('Desktop'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')
) }
foreach ($dir in $targets) {
    $lnk = $shell.CreateShortcut((Join-Path $dir 'ECR Simulator.lnk'))
    if (Test-Path $exe) {
        $lnk.TargetPath = $exe
        $lnk.Arguments = ''
    } else {
        $lnk.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $lnk.Arguments = '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $root 'Start-EcrSimulator.ps1') + '"'
        $lnk.WindowStyle = 7   # minimized: no console flash
    }
    $lnk.WorkingDirectory = $root
    $lnk.IconLocation = $icon + ',0'
    $lnk.Description = 'ECR Simulator - cash register for the Areeba POS ECR link (by Ahmad El Lahib)'
    $lnk.Save()
    Write-Host ('Created ' + $lnk.FullName)
}
Write-Host ''
Write-Host 'Done. Open "ECR Simulator" from the Desktop or the Start menu.'
