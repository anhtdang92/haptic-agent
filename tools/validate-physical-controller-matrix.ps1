[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Xbox','EliteSeries2','DualSense','DualSenseEdge')]
    [string] $Controller,

    [Parameter(Mandatory)]
    [ValidateSet('USB','Bluetooth')]
    [string] $Connection,

    [Parameter(Mandatory)]
    [ValidateSet('GameInput','XInput','DualSenseNative')]
    [string] $Transport,

    [Parameter(Mandatory)]
    [string] $Tester,

    [string] $FirmwareVersion = 'unknown',
    [string] $DeviceInstanceId = 'unknown',
    [string] $BluetoothAdapter = 'not-applicable',
    [string] $UsbController = 'unknown',
    [string] $AppVersion = 'development',
    [string] $GitCommit = 'unknown',
    [string] $OutputDirectory = 'validation-results',
    [int] $BlindTrials = 20,
    [int] $MinimumBlindAccuracyPercent = 90,
    [int] $MinimumComfortMinutes = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($BlindTrials -lt 10) { throw 'BlindTrials must be at least 10.' }
if ($MinimumBlindAccuracyPercent -lt 50 -or $MinimumBlindAccuracyPercent -gt 100) { throw 'MinimumBlindAccuracyPercent must be between 50 and 100.' }
if ($MinimumComfortMinutes -lt 30) { throw 'MinimumComfortMinutes must be at least 30.' }

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$qualificationId = "$Controller-$Connection-$Transport-$timestamp"
$entryDirectory = Join-Path $OutputDirectory $qualificationId
New-Item -ItemType Directory -Force -Path $entryDirectory | Out-Null

$checks = @(
    @{ Id='identity'; Prompt='Verify the detected model, connection, transport, firmware, and device instance ID match this qualification entry.'; Critical=$true },
    @{ Id='connect'; Prompt='Connect the controller. Confirm the connected cue plays exactly once and the UI reports the expected model and transport.'; Critical=$true },
    @{ Id='input-map'; Prompt='Exercise every supported button, stick, trigger, Guide control, and paddle-visible input. Confirm no swaps, duplicates, or phantom input.'; Critical=$true },
    @{ Id='motor-map'; Prompt='Test low-frequency, high-frequency, left-trigger, and right-trigger output independently. Confirm only supported motors activate and channels are not swapped.'; Critical=$true },
    @{ Id='focus-cycle'; Prompt='Squeeze both triggers above 80%. Confirm Focus Mode advances exactly once.'; Critical=$true },
    @{ Id='focus-rearm'; Prompt='Hold both triggers, squeeze again without releasing, then release below 50% and squeeze again. Confirm hysteresis prevents repeats and correctly rearms.'; Critical=$true },
    @{ Id='working-resume'; Prompt='Start agent work, trigger navigation, command, and queue transients, and confirm the working heartbeat resumes after each.'; Critical=$true },
    @{ Id='approval-resume'; Prompt='Create an approval request, trigger every lower-priority transient, and confirm the approval heartbeat always resumes.'; Critical=$true },
    @{ Id='focus-policy'; Prompt='Verify all five Focus Contracts. Approvals, interruptions, and errors must survive every mode; suppressed routine cues must remain suppressed.'; Critical=$true },
    @{ Id='queue'; Prompt='Queue a prompt and overflow the queue. Confirm queued and queue-full cues are distinct without looking at the display.'; Critical=$false },
    @{ Id='voice'; Prompt='Run listening, recognized, no-speech, cancelled, and failed voice outcomes. Confirm each delivered cue matches policy and is distinguishable.'; Critical=$false },
    @{ Id='disconnect-loop'; Prompt='Disconnect during working and approval loops. Confirm all motors stop promptly and no stale cue resumes.'; Critical=$true },
    @{ Id='reconnect'; Prompt='Reconnect after USB removal or Bluetooth loss. Confirm one connection cue, restored input, and restoration of the current persistent semantic state.'; Critical=$true },
    @{ Id='sleep-wake'; Prompt='Put Windows to sleep and resume. Confirm the controller reconnects or reports a clear recoverable state with no stuck motors.'; Critical=$true },
    @{ Id='controller-swap'; Prompt='Replace the active controller while a persistent state is active. Confirm the old device stops and the new device receives only the current state.'; Critical=$true },
    @{ Id='shutdown'; Prompt='Exit CtrlAgent during each persistent state. Confirm all motors and trigger effects stop and no CtrlAgent processes remain.'; Critical=$true },
    @{ Id='blind-recognition'; Prompt="Randomize approval, completion, warning, and error cues for $BlindTrials trials. Enter the number identified correctly."; Critical=$true },
    @{ Id='comfort'; Prompt="Use CtrlAgent for at least $MinimumComfortMinutes minutes with at least 100 cues. Record duration, discomfort, fatigue, numbness, annoyance, and missed cues."; Critical=$true }
)

$results = [System.Collections.Generic.List[object]]::new()
$blindCorrect = $null
$comfortMinutes = $null

foreach ($check in $checks) {
    Write-Host "`n[$($check.Id)] $($check.Prompt)" -ForegroundColor Cyan

    if ($check.Id -eq 'blind-recognition') {
        do {
            $raw = Read-Host "Correct identifications (0-$BlindTrials)"
            $parsed = [int]::TryParse($raw, [ref]$blindCorrect)
        } until ($parsed -and $blindCorrect -ge 0 -and $blindCorrect -le $BlindTrials)
        $accuracy = [Math]::Round(($blindCorrect / $BlindTrials) * 100, 2)
        $result = if ($accuracy -ge $MinimumBlindAccuracyPercent) { 'pass' } else { 'fail' }
        $notes = Read-Host "Notes (accuracy $accuracy%)"
    }
    elseif ($check.Id -eq 'comfort') {
        do {
            $raw = Read-Host 'Actual comfort session duration in minutes'
            $parsed = [int]::TryParse($raw, [ref]$comfortMinutes)
        } until ($parsed -and $comfortMinutes -ge 0)
        $symptoms = Read-Host 'Symptoms or fatigue (enter none only when genuinely none)'
        $missed = Read-Host 'Missed or confusing cues'
        $result = if ($comfortMinutes -ge $MinimumComfortMinutes -and $symptoms.Trim().ToLowerInvariant() -eq 'none') { 'pass' } else { 'fail' }
        $notes = "duration=$comfortMinutes; symptoms=$symptoms; missed=$missed"
    }
    else {
        do {
            $result = (Read-Host 'Result (pass/fail/blocked)').Trim().ToLowerInvariant()
        } until ($result -in @('pass','fail','blocked'))
        $notes = Read-Host 'Notes/evidence reference'
    }

    $results.Add([ordered]@{
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        id = $check.Id
        critical = [bool]$check.Critical
        result = $result
        notes = $notes
    })
}

$failedCritical = @($results | Where-Object { $_.critical -and $_.result -ne 'pass' })
$failedAny = @($results | Where-Object { $_.result -ne 'pass' })
$blindAccuracy = if ($null -ne $blindCorrect) { [Math]::Round(($blindCorrect / $BlindTrials) * 100, 2) } else { 0 }
$qualified = $failedAny.Count -eq 0 -and $blindAccuracy -ge $MinimumBlindAccuracyPercent -and $comfortMinutes -ge $MinimumComfortMinutes

$record = [ordered]@{
    schemaVersion = 2
    qualificationId = $qualificationId
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    qualified = $qualified
    tester = $Tester
    application = [ordered]@{
        version = $AppVersion
        gitCommit = $GitCommit
    }
    environment = [ordered]@{
        machine = $env:COMPUTERNAME
        windows = [Environment]::OSVersion.VersionString
        powershell = $PSVersionTable.PSVersion.ToString()
        bluetoothAdapter = $BluetoothAdapter
        usbController = $UsbController
    }
    controller = [ordered]@{
        model = $Controller
        connection = $Connection
        transport = $Transport
        firmware = $FirmwareVersion
        deviceInstanceId = $DeviceInstanceId
    }
    thresholds = [ordered]@{
        blindTrials = $BlindTrials
        minimumBlindAccuracyPercent = $MinimumBlindAccuracyPercent
        minimumComfortMinutes = $MinimumComfortMinutes
    }
    measurements = [ordered]@{
        blindCorrect = $blindCorrect
        blindAccuracyPercent = $blindAccuracy
        comfortMinutes = $comfortMinutes
    }
    summary = [ordered]@{
        totalChecks = $results.Count
        failedOrBlocked = $failedAny.Count
        failedOrBlockedCritical = $failedCritical.Count
    }
    checks = $results
}

$jsonPath = Join-Path $entryDirectory 'qualification.json'
$markdownPath = Join-Path $entryDirectory 'qualification.md'
$record | ConvertTo-Json -Depth 10 | Set-Content $jsonPath -Encoding utf8

$lines = @(
    "# Controller qualification: $qualificationId",
    '',
    "- Result: **$(if ($qualified) { 'PASS' } else { 'FAIL' })**",
    "- Tester: $Tester",
    "- App: $AppVersion ($GitCommit)",
    "- Controller: $Controller / $Connection / $Transport",
    "- Firmware: $FirmwareVersion",
    "- Blind recognition: $blindCorrect/$BlindTrials ($blindAccuracy%)",
    "- Comfort session: $comfortMinutes minutes",
    '',
    '| Check | Critical | Result | Notes |',
    '|---|:---:|:---:|---|'
)
foreach ($item in $results) {
    $safeNotes = ($item.notes -replace '\|','\\|') -replace "`r?`n", ' '
    $lines += "| $($item.id) | $($item.critical) | $($item.result) | $safeNotes |"
}
$lines | Set-Content $markdownPath -Encoding utf8

$hashes = Get-ChildItem $entryDirectory -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
$hashes | Set-Content (Join-Path $entryDirectory 'SHA256SUMS.txt') -Encoding ascii

Write-Host "`nSaved qualification evidence to $entryDirectory" -ForegroundColor Green
if (-not $qualified) {
    Write-Host "$($failedAny.Count) checks were not marked pass; hardware entry is NOT qualified." -ForegroundColor Red
    exit 1
}

Write-Host 'Physical controller matrix entry qualified.' -ForegroundColor Green
