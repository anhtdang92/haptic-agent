[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DisplayVersion,
    [Parameter(Mandatory)] [version] $PackageVersion,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $OutputRoot = 'artifacts/release',
    [switch] $RequireSigning,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputRootPath = Join-Path $repoRoot $OutputRoot
$staging = Join-Path $outputRootPath 'staging'
$portableRoot = Join-Path $outputRootPath "CtrlAgent-$DisplayVersion-$Runtime"
$installerRoot = Join-Path $outputRootPath 'installer'
$logsRoot = Join-Path $outputRootPath 'logs'

Remove-Item $outputRootPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $staging,$portableRoot,$installerRoot,$logsRoot -Force | Out-Null

$numericVersion = "$($PackageVersion.Major).$($PackageVersion.Minor).$($PackageVersion.Build).$([Math]::Max(0, $PackageVersion.Revision))"
$commonProperties = @(
    "-p:Version=$($PackageVersion.Major).$($PackageVersion.Minor).$($PackageVersion.Build)",
    "-p:FileVersion=$numericVersion",
    "-p:AssemblyVersion=$numericVersion",
    "-p:InformationalVersion=$DisplayVersion+$($env:GITHUB_SHA ?? 'local')",
    '-p:ContinuousIntegrationBuild=true',
    '-p:Deterministic=true',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true'
)

Push-Location $repoRoot
try {
    dotnet publish src/CtrlAgent.App/CtrlAgent.App.csproj --configuration $Configuration --runtime $Runtime --self-contained true --output (Join-Path $staging 'app') @commonProperties 2>&1 |
        Tee-Object (Join-Path $logsRoot 'publish-app.log')
    if ($LASTEXITCODE -ne 0) { throw 'Console publish failed.' }

    dotnet publish src/CtrlAgent.Gui/CtrlAgent.Gui.csproj --configuration $Configuration --runtime $Runtime --self-contained true --output (Join-Path $staging 'gui') @commonProperties 2>&1 |
        Tee-Object (Join-Path $logsRoot 'publish-gui.log')
    if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }

    msbuild native/CtrlAgent.GameInputBridge/CtrlAgent.GameInputBridge.vcxproj /restore /m /p:Configuration=Release /p:Platform=x64 2>&1 |
        Tee-Object (Join-Path $logsRoot 'native-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'GameInput bridge build failed.' }

    Copy-Item (Join-Path $staging 'app\CtrlAgent.App.exe') $portableRoot
    Copy-Item (Join-Path $staging 'gui\CtrlAgent.Gui.exe') $portableRoot

    $bridge = Get-ChildItem (Join-Path $repoRoot 'native\CtrlAgent.GameInputBridge') -Filter 'CtrlAgent.GameInputBridge.exe' -Recurse |
        Where-Object { $_.FullName -match '\\Release\\' } | Select-Object -First 1
    if (-not $bridge) { throw 'CtrlAgent.GameInputBridge.exe was not produced.' }
    Copy-Item $bridge.FullName $portableRoot

    $gameInputDll = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.gameinput') -Filter 'GameInput.dll' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'win-x64|x64' } | Select-Object -First 1
    if ($gameInputDll) { Copy-Item $gameInputDll.FullName $portableRoot }

    Copy-Item README.md,LICENSE $portableRoot
    Copy-Item docs (Join-Path $portableRoot 'docs') -Recurse
    Copy-Item tools/release/Invoke-CtrlAgentRollback.ps1 $portableRoot

    $binaryPaths = Get-ChildItem $portableRoot -File | Where-Object { $_.Extension -in '.exe','.dll' } | Select-Object -ExpandProperty FullName
    & (Join-Path $PSScriptRoot 'Sign-WindowsArtifacts.ps1') -Paths $binaryPaths -RequireSigning:$RequireSigning

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'CtrlAgent'
        displayVersion = $DisplayVersion
        packageVersion = $numericVersion
        runtime = $Runtime
        sourceRevision = ($env:GITHUB_SHA ?? (git rev-parse HEAD))
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        files = @(
            Get-ChildItem $portableRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($portableRoot, $_.FullName).Replace('\','/')
                    bytes = $_.Length
                    sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
        )
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $portableRoot 'release-manifest.json') -Encoding utf8

    $zipPath = Join-Path $outputRootPath "CtrlAgent-$DisplayVersion-$Runtime.zip"
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Create($zipPath)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            Get-ChildItem $portableRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath((Split-Path $portableRoot -Parent), $_.FullName).Replace('\','/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
                $input = [IO.File]::OpenRead($_.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }

    $installerPath = $null
    if (-not $SkipInstaller) {
        $iscc = Get-ChildItem "${env:ProgramFiles(x86)}\Inno Setup *\ISCC.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $iscc) { throw 'Inno Setup compiler was not found.' }

        & $iscc.FullName "/DDisplayVersion=$DisplayVersion" "/DPackageVersion=$numericVersion" "/DStagingDir=$portableRoot" "/DOutputDir=$installerRoot" "/DRepoRoot=$repoRoot" (Join-Path $repoRoot 'installer\CtrlAgent.iss') 2>&1 |
            Tee-Object (Join-Path $logsRoot 'installer-build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

        $installerPath = (Get-ChildItem $installerRoot -Filter 'CtrlAgent-Setup-*.exe' | Select-Object -First 1).FullName
        if (-not $installerPath) { throw 'Installer output was not produced.' }
        & (Join-Path $PSScriptRoot 'Sign-WindowsArtifacts.ps1') -Paths @($installerPath) -RequireSigning:$RequireSigning
    }

    $artifacts = @($zipPath)
    if ($installerPath) { $artifacts += $installerPath }
    $checksumPath = Join-Path $outputRootPath 'SHA256SUMS.txt'
    $artifacts | ForEach-Object {
        $hash = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($_))"
    } | Set-Content $checksumPath -Encoding ascii

    [pscustomobject]@{
        PortableZip = $zipPath
        Installer = $installerPath
        Checksums = $checksumPath
        PortableRoot = $portableRoot
        Logs = $logsRoot
    } | ConvertTo-Json | Set-Content (Join-Path $outputRootPath 'build-outputs.json') -Encoding utf8
}
finally {
    Pop-Location
}
