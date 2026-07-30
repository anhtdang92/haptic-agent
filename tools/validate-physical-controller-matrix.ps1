param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Xbox','EliteSeries2','DualSense','DualSenseEdge')]
    [string]$Controller,

    [Parameter(Mandatory = $true)]
    [ValidateSet('USB','Bluetooth','GameInput','XInput','DualSenseNative')]
    [string]$Transport,

    [string]$OutputDirectory = 'validation-results'
)

$ErrorActionPreference = 'Stop'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$path = Join-Path $OutputDirectory "$Controller-$Transport-$timestamp.csv"

$checks = @(
    @{ Id='connect'; Prompt='Connect the controller. Did the connected cue play exactly once?' },
    @{ Id='focus-cycle'; Prompt='Squeeze both triggers above 80%. Did Focus Mode advance exactly once?' },
    @{ Id='focus-rearm'; Prompt='Keep both triggers held, then squeeze again without releasing. Confirm it did NOT advance twice. Release either below 50%, squeeze again, and confirm one new advance.' },
    @{ Id='working-resume'; Prompt='Start agent work, trigger a short navigation tick, and confirm the working heartbeat resumes.' },
    @{ Id='approval-resume'; Prompt='Create an approval request, trigger navigation and Focus cycling, and confirm the approval heartbeat resumes after each transient cue.' },
    @{ Id='queue'; Prompt='Queue a prompt and then overflow the queue. Confirm queued and queue-full cues are distinct.' },
    @{ Id='voice'; Prompt='Start voice input, complete one successful recognition, then force one failure. Confirm listening, success, and failure cues are distinct.' },
    @{ Id='disconnect-loop'; Prompt='Disconnect during a looping approval or working cue. Confirm motors stop and reconnect succeeds.' },
    @{ Id='shutdown'; Prompt='Exit CtrlAgent. Confirm all motors and trigger effects stop.' },
    @{ Id='blind-recognition'; Prompt='Randomize approval, completion, warning, and error cues for 20 trials. Record correct identifications.' },
    @{ Id='comfort'; Prompt='Run at least 100 cues over 30 minutes. Record discomfort, fatigue, numbness, or missed cues.' }
)

$rows = foreach ($check in $checks) {
    Write-Host "`n[$($check.Id)] $($check.Prompt)" -ForegroundColor Cyan
    $result = Read-Host 'Result (pass/fail/blocked)'
    $notes = Read-Host 'Notes'
    [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        Controller = $Controller
        Transport = $Transport
        Check = $check.Id
        Result = $result
        Notes = $notes
    }
}

$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $path
Write-Host "`nSaved validation evidence to $path" -ForegroundColor Green

$failed = @($rows | Where-Object { $_.Result -ne 'pass' })
if ($failed.Count -gt 0) {
    Write-Host "$($failed.Count) checks were not marked pass." -ForegroundColor Yellow
    exit 1
}

Write-Host 'Physical controller matrix entry passed.' -ForegroundColor Green
