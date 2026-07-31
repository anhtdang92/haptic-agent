[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PortableZip,
    [Parameter(Mandatory)] [string] $Installer,
    [string] $PreviousInstaller,
    [string] $PreviousVersion,
    [string] $ReportPath = 'release-readiness-report.json',
    [switch] $RequireSignature,
    [switch] $SkipInstallerRehearsal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string] $Name, [bool] $Passed, [string] $Detail) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    Write-Host "[$(if ($Passed) {'PASS'} else {'FAIL'})] $Name - $Detail" -ForegroundColor $(if ($Passed) {'Green'} else {'Red'})
}
function Require([bool] $Condition, [string] $Name, [string] $Success, [string] $Failure) {
    Add-Check $Name $Condition $(if ($Condition) { $Success } else { $Failure })
}
function Invoke-Installer([string] $Path, [string] $Directory, [string] $Log) {
    $process = Start-Process $Path -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=$Directory","/LOG=$Log") -Wait -PassThru
    Require ($process.ExitCode -eq 0) "Install $([IO.Path]::GetFileName($Path))" "Exit code 0." "Exit code $($process.ExitCode)."
}
function Test-Launch([string] $Exe, [string] $Name, [string] $EvidenceDirectory) {
    New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
    $startupLog = Join-Path $EvidenceDirectory 'installed-gui-startup.log'
    $eventLog = Join-Path $EvidenceDirectory 'installed-gui-eventlog.txt'
    Remove-Item $startupLog,$eventLog -Force -ErrorAction SilentlyContinue

    $startedUtc = [DateTime]::UtcNow
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Exe)
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['CTRLAGENT_STARTUP_LOG'] = $startupLog
    $process = [Diagnostics.Process]::Start($startInfo)
    Start-Sleep -Seconds 4

    $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    $healthy = -not $process.HasExited -or $exitCode -eq 0
    $diagnostic = if (Test-Path $startupLog) { (Get-Content $startupLog -Raw).Trim() } else { 'No CtrlAgent startup log was produced.' }

    if (-not $healthy) {
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=$startedUtc.AddSeconds(-2) } -ErrorAction Stop |
                Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') -or $_.Message -match 'CtrlAgent' } |
                Select-Object -First 20 TimeCreated,ProviderName,Id,LevelDisplayName,Message
            $events | Format-List | Out-String | Set-Content $eventLog -Encoding utf8
        }
        catch {
            "Unable to collect Windows Application events: $($_.Exception)" | Set-Content $eventLog -Encoding utf8
        }
    }

    $detail = if ($healthy) {
        'Process launched without an immediate nonzero exit.'
    }
    else {
        "Process exited with $exitCode. Startup diagnostics: $diagnostic"
    }
    Require $healthy $Name 'Process launched without an immediate nonzero exit.' $detail
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
function Test-Signature([string] $Path, [string] $Name) {
    $signature = Get-AuthenticodeSignature $Path
    $signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { 'no signer' }
    Require ($signature.Status -eq 'Valid') $Name "Valid signature by $signer." "Signature status: $($signature.Status); signer: $signer."
}

$normalizedVersion = $Version.Trim()
Require ($normalizedVersion -match '^v?\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') 'Semantic version' $normalizedVersion 'Expected MAJOR.MINOR.PATCH with an optional prerelease suffix.'

$zipPath = (Resolve-Path $PortableZip -ErrorAction SilentlyContinue)?.Path
$installerPath = (Resolve-Path $Installer -ErrorAction SilentlyContinue)?.Path
$previousInstallerPath = if ($PreviousInstaller) { (Resolve-Path $PreviousInstaller -ErrorAction SilentlyContinue)?.Path } else { $null }
Require ($null -ne $zipPath) 'Portable archive exists' $PortableZip 'Portable archive was not found.'
Require ($null -ne $installerPath) 'Installer exists' $Installer 'Installer was not found.'

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("CtrlAgent-release-" + [guid]::NewGuid().ToString('N'))
$extractRoot = Join-Path $tempRoot 'portable'
$installRoot = Join-Path $tempRoot 'installed'
$reportDirectory = Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent
$evidenceDirectory = Join-Path $reportDirectory 'logs'
$settingsRoot = Join-Path $env:APPDATA 'CtrlAgent'
$sentinelPath = Join-Path $settingsRoot 'release-qualification-sentinel.json'
New-Item -ItemType Directory -Path $extractRoot,$settingsRoot,$evidenceDirectory -Force | Out-Null
$sentinel = [ordered]@{ id = [guid]::NewGuid().ToString(); createdUtc = [DateTime]::UtcNow.ToString('o') }
$sentinel | ConvertTo-Json | Set-Content $sentinelPath -Encoding utf8

try {
    if ($zipPath) {
        Expand-Archive $zipPath $extractRoot -Force
        $payloadRoots = @(Get-ChildItem $extractRoot -Directory)
        Require ($payloadRoots.Count -eq 1) 'Single package root' "$($payloadRoots.Count) root directory." 'Portable ZIP must contain exactly one root directory.'
        $payload = if ($payloadRoots.Count -eq 1) { $payloadRoots[0].FullName } else { $extractRoot }

        foreach ($file in @('CtrlAgent.Gui.exe','CtrlAgent.App.exe','CtrlAgent.GameInputBridge.exe','README.md','LICENSE','release-manifest.json','Invoke-CtrlAgentRollback.ps1')) {
            Require (Test-Path (Join-Path $payload $file)) "Portable contains $file" $file "$file is missing."
        }
        Require (Test-Path (Join-Path $payload 'docs')) 'Portable documentation' 'docs directory present.' 'docs directory missing.'

        $manifestPath = Join-Path $payload 'release-manifest.json'
        if (Test-Path $manifestPath) {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            $mismatches = @()
            foreach ($file in $manifest.files) {
                $path = Join-Path $payload $file.path
                if (-not (Test-Path $path)) { $mismatches += "missing:$($file.path)"; continue }
                $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actual -ne $file.sha256) { $mismatches += "hash:$($file.path)" }
            }
            $mismatchDetail = if ($mismatches.Count -gt 0) { $mismatches -join '; ' } else { 'No manifest mismatches.' }
            Require ($mismatches.Count -eq 0) 'Payload manifest integrity' 'Every manifested file matches.' $mismatchDetail
        }

        $forbidden = @(Get-ChildItem $payload -Recurse -File | Where-Object {
            $_.Extension -in @('.pdb','.user','.suo','.pfx','.snk') -or
            $_.Name -match '(?i)(secret|token|credential|private[-_]?key)' -or
            $_.FullName -match '(?i)[\\/](bin|obj)[\\/]'
        })
        $forbiddenDetail = if ($forbidden.Count -gt 0) {
            (($forbidden | ForEach-Object { $_.FullName }) -join '; ')
        }
        else {
            'No forbidden files detected.'
        }
        Require ($forbidden.Count -eq 0) 'No sensitive or development files' 'No forbidden files detected.' $forbiddenDetail

        if ($RequireSignature) {
            foreach ($exe in @('CtrlAgent.Gui.exe','CtrlAgent.App.exe','CtrlAgent.GameInputBridge.exe')) {
                Test-Signature (Join-Path $payload $exe) "Signed $exe"
            }
        }
    }

    if ($RequireSignature -and $installerPath) { Test-Signature $installerPath 'Signed installer' }

    if ($installerPath -and -not $SkipInstallerRehearsal) {
        if ($previousInstallerPath) {
            Invoke-Installer $previousInstallerPath $installRoot (Join-Path $tempRoot 'previous-install.log')
            if ($PreviousVersion) {
                $installedProperty = Get-ItemProperty 'HKCU:\Software\CtrlAgent' -ErrorAction SilentlyContinue
                $installed = if ($installedProperty) { $installedProperty.InstalledVersion } else { $null }
                Require ($installed -eq $PreviousVersion) 'Previous version installed' "$installed" "Expected $PreviousVersion, got $installed."
            }
            Require (Test-Path $sentinelPath) 'Settings survive initial install' 'Qualification sentinel preserved.' 'AppData settings were removed.'
        }

        Invoke-Installer $installerPath $installRoot (Join-Path $tempRoot 'current-install.log')
        foreach ($file in @('CtrlAgent.Gui.exe','CtrlAgent.App.exe','CtrlAgent.GameInputBridge.exe')) {
            Require (Test-Path (Join-Path $installRoot $file)) "Installed payload contains $file" $file "$file is missing after installation."
        }
        $installedProperty = Get-ItemProperty 'HKCU:\Software\CtrlAgent' -ErrorAction SilentlyContinue
        $installedVersion = if ($installedProperty) { $installedProperty.InstalledVersion } else { $null }
        Require ($installedVersion -eq $normalizedVersion) 'Installed version registry' "$installedVersion" "Expected $normalizedVersion, got $installedVersion."
        Require (Test-Path $sentinelPath) 'Settings survive upgrade' 'Qualification sentinel preserved.' 'AppData settings were removed during upgrade.'
        Test-Launch (Join-Path $installRoot 'CtrlAgent.Gui.exe') 'Installed GUI launch smoke test' $evidenceDirectory

        if ($previousInstallerPath) {
            & (Join-Path $PSScriptRoot 'release\Invoke-CtrlAgentRollback.ps1') -TargetInstaller $previousInstallerPath -ExpectedVersion $PreviousVersion -InstallDirectory $installRoot -SkipLaunchCheck
            Require (Test-Path $sentinelPath) 'Settings survive rollback' 'Qualification sentinel preserved.' 'AppData settings were removed during rollback.'
            $rollbackProperty = Get-ItemProperty 'HKCU:\Software\CtrlAgent' -ErrorAction SilentlyContinue
            $rolledBackVersion = if ($rollbackProperty) { $rollbackProperty.InstalledVersion } else { $null }
            Require ($rolledBackVersion -eq $PreviousVersion) 'Rollback version verified' "$rolledBackVersion" "Expected $PreviousVersion, got $rolledBackVersion."
            Invoke-Installer $installerPath $installRoot (Join-Path $tempRoot 'reinstall-current.log')
        }

        $uninstaller = Join-Path $installRoot 'unins000.exe'
        Require (Test-Path $uninstaller) 'Uninstaller exists' $uninstaller 'Uninstaller was not created.'
        if (Test-Path $uninstaller) {
            $uninstall = Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
            Require ($uninstall.ExitCode -eq 0) 'Silent uninstall' 'Exit code 0.' "Exit code $($uninstall.ExitCode)."
            Start-Sleep -Seconds 1
            Require (-not (Test-Path (Join-Path $installRoot 'CtrlAgent.Gui.exe'))) 'Uninstall removes binaries' 'Application binaries removed.' 'Application binaries remain.'
            Require (Test-Path $sentinelPath) 'Uninstall preserves user data' 'AppData settings preserved.' 'Uninstall removed user settings.'
        }
    }
    elseif ($SkipInstallerRehearsal) {
        Add-Check 'Installer rehearsal' $false 'Skipped installer rehearsal cannot qualify a release.'
    }

    $artifactPaths = @($zipPath,$installerPath) | Where-Object { $_ }
    $artifacts = @($artifactPaths | ForEach-Object {
        [ordered]@{ file = [IO.Path]::GetFileName($_); bytes = (Get-Item $_).Length; sha256 = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $failed = @($checks | Where-Object { -not $_.passed })
    $qualified = $failed.Count -eq 0 -and -not $SkipInstallerRehearsal
    [ordered]@{
        schemaVersion = 3
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        version = $normalizedVersion
        previousVersion = $PreviousVersion
        qualifiedForStableRelease = $qualified
        signatureRequired = [bool]$RequireSignature
        runner = [ordered]@{ os = [Environment]::OSVersion.VersionString; machine = $env:COMPUTERNAME; powershell = $PSVersionTable.PSVersion.ToString() }
        artifacts = $artifacts
        diagnosticFiles = @(Get-ChildItem $evidenceDirectory -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
        checks = $checks
    } | ConvertTo-Json -Depth 10 | Set-Content $ReportPath -Encoding utf8

    $artifacts | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content 'SHA256SUMS.txt' -Encoding ascii
    if (-not $qualified) { throw "Release readiness failed with $($failed.Count) blocking check(s)." }
    Write-Host 'Release artifacts qualify for publication.' -ForegroundColor Green
}
finally {
    Remove-Item $sentinelPath -Force -ErrorAction SilentlyContinue
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
