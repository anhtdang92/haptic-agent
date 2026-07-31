[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [switch]$AllowExperimental
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Error "HARDWARE QUALIFICATION FAILED: $Message"
    exit 1
}

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    Fail "Report not found: $ReportPath"
}

try {
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
}
catch {
    Fail "Report is not valid JSON: $($_.Exception.Message)"
}

$requiredTopLevel = @(
    'schemaVersion',
    'device',
    'environment',
    'transport',
    'tests',
    'privacy',
    'recommendation'
)

foreach ($field in $requiredTopLevel) {
    if ($null -eq $report.PSObject.Properties[$field]) {
        Fail "Missing top-level field '$field'."
    }
}

if ($report.schemaVersion -ne 1) {
    Fail "Unsupported schemaVersion '$($report.schemaVersion)'; expected 1."
}

foreach ($field in @('family', 'model', 'firmwareVersion')) {
    if ([string]::IsNullOrWhiteSpace([string]$report.device.$field)) {
        Fail "device.$field is required."
    }
}

foreach ($field in @('windowsBuild', 'applicationVersion', 'driverStack')) {
    if ([string]::IsNullOrWhiteSpace([string]$report.environment.$field)) {
        Fail "environment.$field is required."
    }
}

if ([string]::IsNullOrWhiteSpace([string]$report.transport.kind)) {
    Fail 'transport.kind is required.'
}

if ($report.privacy.containsUniqueDeviceIdentifiers -ne $false) {
    Fail 'privacy.containsUniqueDeviceIdentifiers must be false.'
}

$requiredTests = @(
    'discoveryBeforeLaunch',
    'discoveryAfterLaunch',
    'identityStable',
    'standardControls',
    'simultaneousInput',
    'hapticsDistinct',
    'hapticsStopCleanly',
    'disconnectReconnect',
    'focusBackgroundBehaviorDocumented',
    'soakTest'
)

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($testName in $requiredTests) {
    $property = $report.tests.PSObject.Properties[$testName]
    if ($null -eq $property) {
        $failures.Add("Missing test '$testName'.")
        continue
    }

    $result = $property.Value
    if ($result.status -notin @('pass', 'fail', 'blocked', 'not-applicable')) {
        $failures.Add("Test '$testName' has invalid status '$($result.status)'.")
    }

    if ([string]::IsNullOrWhiteSpace([string]$result.evidence)) {
        $failures.Add("Test '$testName' has no evidence note.")
    }

    if ($result.status -eq 'fail') {
        $failures.Add("Test '$testName' failed: $($result.evidence)")
    }

    if ($result.status -eq 'blocked' -and -not $AllowExperimental) {
        $failures.Add("Test '$testName' is blocked: $($result.evidence)")
    }
}

$soak = $report.tests.soakTest
if ($soak.status -eq 'pass') {
    if ([int]$soak.durationMinutes -lt 30) {
        $failures.Add('soakTest must run for at least 30 minutes.')
    }
    if ([int]$soak.hapticCueCount -lt 100) {
        $failures.Add('soakTest must include at least 100 haptic cues.')
    }
    if ([int]$soak.reconnectCount -lt 1) {
        $failures.Add('soakTest must include at least one disconnect/reconnect cycle.')
    }
}

if ($report.tests.standardControls.status -eq 'pass') {
    $requiredControls = @(
        'a', 'b', 'x', 'y', 'menu', 'view',
        'dpadUp', 'dpadDown', 'dpadLeft', 'dpadRight',
        'leftShoulder', 'rightShoulder',
        'leftStickClick', 'rightStickClick',
        'leftTrigger', 'rightTrigger',
        'leftStickX', 'leftStickY', 'rightStickX', 'rightStickY'
    )

    foreach ($control in $requiredControls) {
        if ($report.tests.standardControls.controls.$control -ne $true) {
            $failures.Add("Standard control '$control' was not verified.")
        }
    }
}

$capabilities = $report.tests.capabilities
if ($null -eq $capabilities) {
    $failures.Add("Missing test 'capabilities'.")
}
else {
    foreach ($field in @('guideButton', 'independentPaddles', 'mainRumble', 'triggerRumble', 'adaptiveTriggers', 'lightbar')) {
        if ($null -eq $capabilities.PSObject.Properties[$field]) {
            $failures.Add("Missing capabilities.$field.")
        }
    }
}

if ($report.recommendation -eq 'qualified') {
    if ($failures.Count -gt 0) {
        $failures.Add("recommendation is 'qualified' despite failed qualification gates.")
    }
}
elseif ($report.recommendation -eq 'experimental') {
    if (-not $AllowExperimental) {
        $failures.Add("recommendation is 'experimental'; rerun with -AllowExperimental only for non-release development checks.")
    }
}
else {
    $failures.Add("recommendation must be 'qualified', 'experimental', or 'unsupported'.")
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'Hardware qualification failures:' -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host " - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host ''
Write-Host 'HARDWARE QUALIFICATION PASSED' -ForegroundColor Green
Write-Host "Device:    $($report.device.family) $($report.device.model)"
Write-Host "Transport: $($report.transport.kind)"
Write-Host "Result:    $($report.recommendation)"
exit 0
