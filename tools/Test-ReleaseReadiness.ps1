[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PortableZip,
    [Parameter(Mandatory)] [string] $Installer,
    [string] $ReportPath = "release-readiness-report.json",
    [switch] $SkipInstallerRehearsal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$checks = [System.Collections.Generic.List[object]]::new()
function Add-Check([string] $Name, [bool] $Passed, [string] $Detail) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    if ($Passed) { Write-Host "[PASS] $Name - $Detail" -ForegroundColor Green }
    else { Write-Host "[FAIL] $Name - $Detail" -ForegroundColor Red }
}

function Require([bool] $Condition, [string] $Name, [string] $Success, [string] $Failure) {
    Add-Check $Name $Condition ($(if ($Condition) { $Success } else { $Failure }))
}

$normalizedVersion = $Version.Trim()
Require ($normalizedVersion -match '^v\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') `
    'Semantic version' $normalizedVersion 'Expected vMAJOR.MINOR.PATCH with an optional prerelease suffix.'

$zipPath = (Resolve-Path $PortableZip -ErrorAction SilentlyContinue)?.Path
$installerPath = (Resolve-Path $Installer -ErrorAction SilentlyContinue)?.Path
Require ($null -ne $zipPath) 'Portable archive exists' $PortableZip 'Portable archive was not found.'
Require ($null -ne $installerPath) 'Installer exists' $Installer 'Installer was not found.'

$expectedZipName = "CtrlAgent-$normalizedVersion-win-x64.zip"
$expectedInstallerName = "CtrlAgent-Setup-$normalizedVersion.exe"
if ($zipPath) {
    Require ([IO.Path]::GetFileName($zipPath) -eq $expectedZipName) 'Portable filename' $expectedZipName "Expected $expectedZipName."
}
if ($installerPath) {
    Require ([IO.Path]::GetFileName($installerPath) -eq $expectedInstallerName) 'Installer filename' $expectedInstallerName "Expected $expectedInstallerName."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("CtrlAgent-release-" + [guid]::NewGuid().ToString('N'))
$extractRoot = Join-Path $tempRoot 'portable'
$installRoot = Join-Path $tempRoot 'installed'
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

try {
    if ($zipPath) {
        Expand-Archive -Path $zipPath -DestinationPath $extractRoot -Force
        $payloadRoots = Get-ChildItem $extractRoot -Directory
        Require ($payloadRoots.Count -eq 1) 'Single package root' "$($payloadRoots.Count) package root found." 'Portable zip must contain exactly one top-level package directory.'
        $payload = if ($payloadRoots.Count -eq 1) { $payloadRoots[0].FullName } else { $extractRoot }

        $requiredFiles = @(
            'CtrlAgent.Gui.exe',
            'CtrlAgent.App.exe',
            'CtrlAgent.GameInputBridge.exe',
            'README.md',
            'LICENSE'
        )
        foreach ($file in $requiredFiles) {
            Require (Test-Path (Join-Path $payload $file)) "Portable contains $file" $file "$file is missing from the portable package."
        }
        Require (Test-Path (Join-Path $payload 'docs')) 'Portable contains documentation' 'docs directory present.' 'docs directory is missing.'

        $forbidden = Get-ChildItem $payload -Recurse -File | Where-Object {
            $_.Extension -in @('.pdb', '.user', '.suo') -or
            $_.Name -match '(?i)(secret|token|credential|private[-_]?key)' -or
            $_.FullName -match '(?i)[\\/](bin|obj)[\\/]'
        }
        Require ($forbidden.Count -eq 0) 'No development or sensitive files' 'No forbidden files detected.' (($forbidden.FullName -join '; '))

        foreach ($exe in @('CtrlAgent.Gui.exe', 'CtrlAgent.App.exe', 'CtrlAgent.GameInputBridge.exe')) {
            $exePath = Join-Path $payload $exe
            if (Test-Path $exePath) {
                $header = [IO.File]::ReadAllBytes($exePath)[0..1]
                Require ($header[0] -eq 0x4D -and $header[1] -eq 0x5A) "$exe is a Windows PE" 'MZ header present.' 'Executable does not have an MZ header.'
            }
        }
    }

    if ($installerPath -and -not $SkipInstallerRehearsal) {
        $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installRoot")
        $installProcess = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru
        Require ($installProcess.ExitCode -eq 0) 'Silent installer succeeds' "Exit code $($installProcess.ExitCode)." "Installer exited with $($installProcess.ExitCode)."

        foreach ($file in @('CtrlAgent.Gui.exe', 'CtrlAgent.App.exe', 'CtrlAgent.GameInputBridge.exe')) {
            Require (Test-Path (Join-Path $installRoot $file)) "Installed payload contains $file" $file "$file is missing after installation."
        }

        $uninstaller = Join-Path $installRoot 'unins000.exe'
        Require (Test-Path $uninstaller) 'Uninstaller exists' $uninstaller 'Inno Setup uninstaller was not created.'
        if (Test-Path $uninstaller) {
            $uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
            Require ($uninstallProcess.ExitCode -eq 0) 'Silent uninstall succeeds' "Exit code $($uninstallProcess.ExitCode)." "Uninstaller exited with $($uninstallProcess.ExitCode)."
            Start-Sleep -Milliseconds 500
            Require (-not (Test-Path (Join-Path $installRoot 'CtrlAgent.Gui.exe'))) 'Uninstall removes application binaries' 'Application binaries removed.' 'Application binaries remain after uninstall.'
        }
    } elseif ($SkipInstallerRehearsal) {
        Add-Check 'Installer rehearsal' $true 'Explicitly skipped; this result cannot qualify a stable release.'
    }

    $artifacts = @()
    foreach ($path in @($zipPath, $installerPath) | Where-Object { $_ }) {
        $hash = Get-FileHash -Path $path -Algorithm SHA256
        $artifacts += [pscustomobject]@{
            file = [IO.Path]::GetFileName($path)
            bytes = (Get-Item $path).Length
            sha256 = $hash.Hash.ToLowerInvariant()
        }
    }

    $failed = @($checks | Where-Object { -not $_.passed })
    $qualified = $failed.Count -eq 0 -and -not $SkipInstallerRehearsal
    $report = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        version = $normalizedVersion
        qualifiedForStableRelease = $qualified
        installerRehearsalSkipped = [bool]$SkipInstallerRehearsal
        runner = [ordered]@{
            os = [Environment]::OSVersion.VersionString
            machine = $env:COMPUTERNAME
            powershell = $PSVersionTable.PSVersion.ToString()
        }
        artifacts = $artifacts
        checks = $checks
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $ReportPath -Encoding utf8

    if ($artifacts.Count -gt 0) {
        $artifacts | ForEach-Object { "$($_.sha256)  $($_.file)" } | Set-Content -Path 'SHA256SUMS.txt' -Encoding ascii
    }

    if (-not $qualified) {
        throw "Release readiness failed: $($failed.Count) blocking check(s). See $ReportPath."
    }

    Write-Host "Release artifacts qualify for stable publication." -ForegroundColor Green
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
