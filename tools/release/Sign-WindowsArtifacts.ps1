[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $Paths,
    [switch] $RequireSigning,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$certificateBase64 = $env:WINDOWS_SIGNING_CERTIFICATE_BASE64
$certificatePassword = $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD

if ([string]::IsNullOrWhiteSpace($certificateBase64) -or [string]::IsNullOrWhiteSpace($certificatePassword)) {
    if ($RequireSigning) {
        throw 'Release signing is required, but WINDOWS_SIGNING_CERTIFICATE_BASE64 or WINDOWS_SIGNING_CERTIFICATE_PASSWORD is missing.'
    }
    Write-Warning 'Signing credentials are not configured. Candidate artifacts remain unsigned.'
    return
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if (-not $signtool) {
    throw 'signtool.exe was not found in the Windows SDK.'
}

$tempPfx = Join-Path ([IO.Path]::GetTempPath()) ("ctrlagent-signing-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($certificateBase64))

    foreach ($path in $Paths) {
        $resolved = (Resolve-Path $path).Path
        & $signtool.FullName sign /fd SHA256 /f $tempPfx /p $certificatePassword /tr $TimestampUrl /td SHA256 $resolved
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $resolved." }

        & $signtool.FullName verify /pa /all /v $resolved
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $resolved." }

        $signature = Get-AuthenticodeSignature $resolved
        if ($signature.Status -ne 'Valid') {
            throw "Signature status for $resolved is $($signature.Status)."
        }
        Write-Host "Signed and verified $resolved" -ForegroundColor Green
    }
}
finally {
    if (Test-Path $tempPfx) {
        Remove-Item $tempPfx -Force
    }
}
