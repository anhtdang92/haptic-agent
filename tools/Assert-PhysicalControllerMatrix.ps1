[CmdletBinding()]
param(
    [string] $EvidenceRoot = 'validation-results',
    [string] $ReportPath = 'validation-results/controller-matrix-report.json',
    [int] $MaximumEvidenceAgeDays = 180,
    [switch] $RequireSameAppVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$required = @(
    @{ controller = 'Xbox'; connection = 'USB'; transport = 'GameInput' },
    @{ controller = 'Xbox'; connection = 'Bluetooth'; transport = 'GameInput' },
    @{ controller = 'EliteSeries2'; connection = 'USB'; transport = 'GameInput' },
    @{ controller = 'EliteSeries2'; connection = 'Bluetooth'; transport = 'GameInput' },
    @{ controller = 'DualSense'; connection = 'USB'; transport = 'DualSenseNative' },
    @{ controller = 'DualSense'; connection = 'Bluetooth'; transport = 'DualSenseNative' },
    @{ controller = 'DualSenseEdge'; connection = 'USB'; transport = 'DualSenseNative' },
    @{ controller = 'DualSenseEdge'; connection = 'Bluetooth'; transport = 'DualSenseNative' }
)

$requiredChecks = @(
    'connect','identity','inputs','motors','focus-cycle','focus-rearm',
    'working-resume','approval-resume','focus-contracts','queue','voice',
    'disconnect-loop','reconnect','sleep-wake','controller-swap','shutdown',
    'blind-recognition','comfort'
)

$cutoff = [DateTime]::UtcNow.AddDays(-$MaximumEvidenceAgeDays)
$documents = @(
    Get-ChildItem -Path $EvidenceRoot -Filter qualification.json -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $json = Get-Content $_.FullName -Raw | ConvertFrom-Json -Depth 20
                [pscustomobject]@{ path = $_.FullName; json = $json; error = $null }
            }
            catch {
                [pscustomobject]@{ path = $_.FullName; json = $null; error = $_.Exception.Message }
            }
        }
)

$rows = foreach ($entry in $required) {
    $matches = @($documents | Where-Object {
        $_.json -and
        $_.json.controller -eq $entry.controller -and
        $_.json.connection -eq $entry.connection -and
        $_.json.transport -eq $entry.transport
    } | Sort-Object { [DateTime]$_.json.completedUtc } -Descending)

    $selected = $matches | Select-Object -First 1
    $issues = [System.Collections.Generic.List[string]]::new()
    if (-not $selected) {
        $issues.Add('missing evidence')
    }
    else {
        $e = $selected.json
        $completed = [DateTime]$e.completedUtc
        if ($completed.ToUniversalTime() -lt $cutoff) { $issues.Add("evidence older than $MaximumEvidenceAgeDays days") }
        if (-not $e.qualified) { $issues.Add('qualification flag is false') }
        if ([string]::IsNullOrWhiteSpace([string]$e.tester)) { $issues.Add('tester missing') }
        if ([string]::IsNullOrWhiteSpace([string]$e.firmwareVersion)) { $issues.Add('firmware missing') }
        if ([string]::IsNullOrWhiteSpace([string]$e.deviceInstanceId)) { $issues.Add('device instance id missing') }
        if ([string]::IsNullOrWhiteSpace([string]$e.appVersion)) { $issues.Add('app version missing') }
        if ([string]::IsNullOrWhiteSpace([string]$e.gitCommit)) { $issues.Add('git commit missing') }
        if ($e.connection -eq 'Bluetooth' -and [string]::IsNullOrWhiteSpace([string]$e.bluetoothAdapter)) { $issues.Add('Bluetooth adapter missing') }
        if ($e.connection -eq 'USB' -and [string]::IsNullOrWhiteSpace([string]$e.usbController)) { $issues.Add('USB controller missing') }

        $checks = @($e.checks)
        foreach ($check in $requiredChecks) {
            $record = @($checks | Where-Object id -eq $check)
            if ($record.Count -ne 1) { $issues.Add("check '$check' missing or duplicated"); continue }
            if ($record[0].result -ne 'pass') { $issues.Add("check '$check' did not pass") }
        }

        if ([int]$e.blindRecognition.trials -lt 20) { $issues.Add('blind-recognition trials below 20') }
        if ([double]$e.blindRecognition.accuracyPercent -lt 90) { $issues.Add('blind-recognition accuracy below 90%') }
        if ([int]$e.comfort.minutes -lt 30) { $issues.Add('comfort duration below 30 minutes') }
        if ([bool]$e.comfort.discomfortReported) { $issues.Add('comfort session reported discomfort') }
    }

    [pscustomobject]@{
        controller = $entry.controller
        connection = $entry.connection
        transport = $entry.transport
        qualified = $issues.Count -eq 0
        evidence = if ($selected) { $selected.path } else { $null }
        completedUtc = if ($selected) { $selected.json.completedUtc } else { $null }
        appVersion = if ($selected) { $selected.json.appVersion } else { $null }
        gitCommit = if ($selected) { $selected.json.gitCommit } else { $null }
        issues = @($issues)
    }
}

if ($RequireSameAppVersion) {
    $versions = @($rows | Where-Object qualified | Select-Object -ExpandProperty appVersion -Unique)
    if ($versions.Count -gt 1) {
        foreach ($row in $rows) {
            $row.qualified = $false
            $row.issues += "matrix mixes app versions: $($versions -join ', ')"
        }
    }
}

$invalidDocuments = @($documents | Where-Object error | ForEach-Object {
    [pscustomobject]@{ path = $_.path; error = $_.error }
})
$qualified = @($rows | Where-Object { -not $_.qualified }).Count -eq 0 -and $invalidDocuments.Count -eq 0

$report = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    evidenceRoot = (Resolve-Path $EvidenceRoot -ErrorAction SilentlyContinue)?.Path
    maximumEvidenceAgeDays = $MaximumEvidenceAgeDays
    requireSameAppVersion = [bool]$RequireSameAppVersion
    qualified = $qualified
    requiredEntries = $rows
    invalidDocuments = $invalidDocuments
}

$directory = Split-Path -Parent $ReportPath
if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
$report | ConvertTo-Json -Depth 20 | Set-Content $ReportPath -Encoding utf8

$rows | Format-Table controller,connection,transport,qualified,appVersion,completedUtc -AutoSize
if (-not $qualified) {
    foreach ($row in $rows | Where-Object { -not $_.qualified }) {
        Write-Host "[$($row.controller)/$($row.connection)] $($row.issues -join '; ')" -ForegroundColor Red
    }
    foreach ($invalid in $invalidDocuments) {
        Write-Host "[invalid] $($invalid.path): $($invalid.error)" -ForegroundColor Red
    }
    throw 'Physical controller qualification matrix is incomplete or invalid.'
}

Write-Host 'Physical controller qualification matrix is complete and valid.' -ForegroundColor Green
