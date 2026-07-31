[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TargetInstaller,
    [string] $ExpectedVersion,
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CtrlAgent'),
    [switch] $SkipLaunchCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$target = (Resolve-Path $TargetInstaller).Path
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) ("CtrlAgent-rollback-" + [guid]::NewGuid().ToString('N'))
$roaming = Join-Path $env:APPDATA 'CtrlAgent'
$local = Join-Path $env:LOCALAPPDATA 'CtrlAgent'
$receiptPath = Join-Path $backupRoot 'rollback-receipt.json'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

function Backup-Directory([string] $Source, [string] $Name) {
    if (Test-Path $Source) {
        Copy-Item $Source (Join-Path $backupRoot $Name) -Recurse -Force
    }
}

function Restore-Directory([string] $Backup, [string] $Destination) {
    if (Test-Path $Backup) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        Copy-Item (Join-Path $Backup '*') $Destination -Recurse -Force
    }
}

Backup-Directory $roaming 'Roaming'
Backup-Directory $local 'Local'

$started = [DateTime]::UtcNow
try {
    Get-Process 'CtrlAgent.Gui','CtrlAgent.App','CtrlAgent.GameInputBridge' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $uninstaller = Join-Path $InstallDirectory 'unins000.exe'
    if (Test-Path $uninstaller) {
        $uninstall = Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) {
            throw "Current CtrlAgent uninstall failed with exit code $($uninstall.ExitCode)."
        }
    }

    $installLog = Join-Path $backupRoot 'rollback-install.log'
    $install = Start-Process $target -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=$InstallDirectory", "/LOG=$installLog"
    ) -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "Rollback installer failed with exit code $($install.ExitCode)."
    }

    Restore-Directory (Join-Path $backupRoot 'Roaming') $roaming
    Restore-Directory (Join-Path $backupRoot 'Local') $local

    $gui = Join-Path $InstallDirectory 'CtrlAgent.Gui.exe'
    if (-not (Test-Path $gui)) {
        throw 'Rollback completed without CtrlAgent.Gui.exe in the install directory.'
    }

    $installedVersion = (Get-ItemProperty 'HKCU:\Software\CtrlAgent' -ErrorAction SilentlyContinue).InstalledVersion
    if ($ExpectedVersion -and $installedVersion -ne $ExpectedVersion) {
        throw "Rollback installed '$installedVersion'; expected '$ExpectedVersion'."
    }

    if (-not $SkipLaunchCheck) {
        $process = Start-Process $gui -PassThru
        Start-Sleep -Seconds 4
        if ($process.HasExited -and $process.ExitCode -ne 0) {
            throw "Rolled-back CtrlAgent exited immediately with code $($process.ExitCode)."
        }
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    }

    $receipt = [ordered]@{
        schemaVersion = 1
        startedUtc = $started.ToString('o')
        completedUtc = [DateTime]::UtcNow.ToString('o')
        targetInstaller = $target
        expectedVersion = $ExpectedVersion
        installedVersion = $installedVersion
        installDirectory = $InstallDirectory
        settingsRestored = $true
        success = $true
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content $receiptPath -Encoding utf8
    Copy-Item $receiptPath (Join-Path $InstallDirectory 'last-rollback-receipt.json') -Force
    Write-Host "CtrlAgent rollback completed: $installedVersion" -ForegroundColor Green
}
catch {
    Restore-Directory (Join-Path $backupRoot 'Roaming') $roaming
    Restore-Directory (Join-Path $backupRoot 'Local') $local
    throw
}
finally {
    Write-Host "Rollback evidence: $backupRoot"
}
