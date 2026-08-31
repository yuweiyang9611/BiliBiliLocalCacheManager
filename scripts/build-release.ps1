[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputRoot = "artifacts/release",
    [switch]$SkipTests,
    [switch]$RunFfmpegIntegrationTests,
    [switch]$SkipElectronSmoke
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
Set-Location -LiteralPath $repositoryRoot

if ($SkipTests -and $RunFfmpegIntegrationTests) {
    throw "-SkipTests and -RunFfmpegIntegrationTests cannot be used together."
}

function Assert-NativeCommandSucceeded {
    param([Parameter(Mandatory = $true)][string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Assert-NonEmptyFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.PSIsContainer -or $item.Length -le 0) {
        throw "Expected a non-empty release file: $Path"
    }
}

function Invoke-CliSmokeTest {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published CLI executable is missing: $ExecutablePath"
    }

    $output = @(& $ExecutablePath --help 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Published CLI smoke test failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }
}

function Invoke-HostSmokeTest {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published Desktop Host executable is missing: $ExecutablePath"
    }

    $request = '{"id":"release-smoke","method":"health","params":{}}'
    $output = @($request | & $ExecutablePath --json-lines 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Published Desktop Host smoke test failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }

    $responseLine = @($output | Where-Object {
        $_ -is [string] -and $_.TrimStart().StartsWith('{', [StringComparison]::Ordinal)
    } | Select-Object -Last 1)
    if ($responseLine.Count -ne 1) {
        throw "Desktop Host smoke test did not return one JSON response."
    }

    $response = $responseLine[0] | ConvertFrom-Json
    if ($response.id -ne "release-smoke" -or
        $response.result.status -notin @("ok", "degraded")) {
        throw "Desktop Host smoke test returned an unexpected response: $($responseLine[0])"
    }
}

function Invoke-PackagedElectronSmokeTest {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Packaged Electron executable is missing: $ExecutablePath"
    }

    $isLinuxPlatform = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [Runtime.InteropServices.OSPlatform]::Linux)
    if ($isLinuxPlatform -and $env:GITHUB_ACTIONS -eq 'true') {
        # GitHub-hosted Linux runners do not preserve the root ownership required
        # by Chromium's SUID sandbox when electron-builder creates linux-unpacked.
        $sandboxPath = Join-Path (Split-Path -Parent $ExecutablePath) 'chrome-sandbox'
        if (-not (Test-Path -LiteralPath $sandboxPath -PathType Leaf)) {
            throw "Packaged Electron SUID sandbox is missing: $sandboxPath"
        }

        & sudo chown root:root -- $sandboxPath
        Assert-NativeCommandSucceeded 'Electron SUID sandbox ownership configuration'
        & sudo chmod 4755 -- $sandboxPath
        Assert-NativeCommandSucceeded 'Electron SUID sandbox mode configuration'
        $sandboxState = [string](& stat '--format=%U:%G %a' -- $sandboxPath)
        Assert-NativeCommandSucceeded 'Electron SUID sandbox state verification'
        if ($sandboxState.Trim() -ne 'root:root 4755') {
            throw "Packaged Electron SUID sandbox has unexpected ownership or mode: $sandboxState"
        }
    }

    $previousHostOverride = $env:CACHE_MANAGER_HOST_PATH
    $previousDotnetOverride = $env:CACHE_MANAGER_DOTNET_PATH
    $missingOverrideRoot = Join-Path `
        ([IO.Path]::GetTempPath()) `
        "blcm-release-smoke-missing-$([Guid]::NewGuid().ToString('N'))"
    try {
        $env:CACHE_MANAGER_HOST_PATH = Join-Path $missingOverrideRoot "missing-host"
        $env:CACHE_MANAGER_DOTNET_PATH = Join-Path $missingOverrideRoot "missing-dotnet"

        if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::Windows)) {
            # Windows Electron binaries use the GUI subsystem, so PowerShell's call
            # operator does not reliably wait for the browser process or expose its
            # exit code. Start-Process -Wait observes the real smoke-test result.
            $process = Start-Process `
                -FilePath $ExecutablePath `
                -ArgumentList '--smoke-test' `
                -WindowStyle Hidden `
                -Wait `
                -PassThru
            if ($process.ExitCode -ne 0) {
                throw "Packaged Electron smoke test failed with exit code $($process.ExitCode)."
            }
            return
        }

        $output = @(& $ExecutablePath --smoke-test 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "Packaged Electron smoke test failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
        }
        if (($output -join "`n") -notmatch '\[smoke\] READY renderer-bootstrap-v1') {
            throw "Packaged Electron smoke test did not report a healthy renderer and Host.`n$($output -join [Environment]::NewLine)"
        }
    }
    finally {
        $env:CACHE_MANAGER_HOST_PATH = $previousHostOverride
        $env:CACHE_MANAGER_DOTNET_PATH = $previousDotnetOverride
    }
}

function Assert-ElectronFuses {
    param(
        [Parameter(Mandatory = $true)][string]$DesktopPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    Push-Location -LiteralPath $DesktopPath
    try {
        $output = @(& npx --no-install electron-fuses read --app $ExecutablePath 2>&1)
        Assert-NativeCommandSucceeded "Electron fuse inspection"
    }
    finally {
        Pop-Location
    }

    $text = $output -join "`n"
    $expectedFuses = @(
        'RunAsNode is Disabled',
        'EnableCookieEncryption is Enabled',
        'EnableNodeOptionsEnvironmentVariable is Disabled',
        'EnableNodeCliInspectArguments is Disabled',
        'EnableEmbeddedAsarIntegrityValidation is Enabled',
        'OnlyLoadAppFromAsar is Enabled',
        'LoadBrowserProcessSpecificV8Snapshot is Disabled',
        'GrantFileProtocolExtraPrivileges is Disabled',
        'WasmTrapHandlers is Enabled'
    )
    $reportedFuses = @($output | ForEach-Object { [string]$_ } | Where-Object {
        $_ -match '^\s+\S+ is (Enabled|Disabled|Removed|Inherited)$'
    })
    if ($reportedFuses.Count -ne $expectedFuses.Count) {
        throw "Packaged Electron fuse verification expected $($expectedFuses.Count) named fuses, found $($reportedFuses.Count).`n$text"
    }
    foreach ($expected in $expectedFuses) {
        if ($text -notmatch [Regex]::Escape($expected)) {
            throw "Packaged Electron fuse verification failed; missing '$expected'.`n$text"
        }
    }
}

$isWindowsPlatform = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
$isLinuxPlatform = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Linux)
if (-not ($isWindowsPlatform -or $isLinuxPlatform)) {
    throw "Release packaging is supported only on Windows and Linux."
}
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne
    [Runtime.InteropServices.Architecture]::X64) {
    throw "Release packaging requires an x64 host process."
}

$runtimeIdentifier = if ($isWindowsPlatform) { "win-x64" } else { "linux-x64" }
$executableExtension = if ($isWindowsPlatform) { ".exe" } else { "" }
$desktopTarget = if ($isWindowsPlatform) { "dist:win" } else { "dist:linux" }
$platformStringComparison = if ($isWindowsPlatform) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}

$dotnetVersion = [string](& dotnet --version)
Assert-NativeCommandSucceeded "dotnet --version"
if ($dotnetVersion.Trim() -ne "10.0.400") {
    throw "Release builds require .NET SDK 10.0.400; found $($dotnetVersion.Trim())."
}

$nodeVersion = [string](& node --version)
Assert-NativeCommandSucceeded "node --version"
if ($nodeVersion.Trim() -notmatch '^v24\.') {
    throw "Release builds require Node.js 24; found $($nodeVersion.Trim())."
}

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$declaredVersion = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $declaredVersion
}

$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}
if (-not [string]::Equals($Version, $declaredVersion, $platformStringComparison)) {
    throw "Release version $Version does not match Directory.Build.props version $declaredVersion"
}

$desktopPath = Join-Path $repositoryRoot "BiliBiliLocalCacheManager.Desktop"
$desktopPackagePath = Join-Path $desktopPath "package.json"
$desktopPackage = Get-Content -LiteralPath $desktopPackagePath -Raw | ConvertFrom-Json
if (-not [string]::Equals([string]$desktopPackage.version, $Version, $platformStringComparison)) {
    throw "Desktop package version $($desktopPackage.version) does not match release version $Version"
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
$outputIsArtifactsRoot = [string]::Equals(
    $outputPath,
    $artifactsRoot,
    $platformStringComparison)
$outputIsInsideArtifacts = $outputPath.StartsWith(
    $artifactsPrefix,
    $platformStringComparison)
if (-not ($outputIsArtifactsRoot -or $outputIsInsideArtifacts)) {
    throw "Output path must stay inside the repository artifacts directory: $outputPath"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

$desktopReleasePath = Join-Path $desktopPath "release"
if (Test-Path -LiteralPath $desktopReleasePath) {
    Remove-Item -LiteralPath $desktopReleasePath -Recurse -Force
}

$hostPublishPath = Join-Path $repositoryRoot "BiliBiliLocalCacheManager.Desktop.Host/bin/Release/net10.0/publish"
if (Test-Path -LiteralPath $hostPublishPath) {
    Remove-Item -LiteralPath $hostPublishPath -Recurse -Force
}

$stagingRoot = Join-Path $outputPath "staging"
$cliStage = Join-Path $stagingRoot "cli-$runtimeIdentifier"
New-Item -ItemType Directory -Path $cliStage -Force | Out-Null

dotnet restore BiliBiliLocalCacheManager.slnx --nologo
Assert-NativeCommandSucceeded "dotnet restore"

if (-not $SkipTests) {
    dotnet build BiliBiliLocalCacheManager.slnx --configuration Release --no-restore --nologo
    Assert-NativeCommandSucceeded "dotnet build"

    $testFilter = if ($isWindowsPlatform) {
        "Category!=FFmpegIntegration"
    }
    else {
        "Category!=FFmpegIntegration&Category!=WindowsOnly"
    }
    dotnet test BiliBiliLocalCacheManager.slnx `
        --configuration Release `
        --no-build `
        --no-restore `
        --nologo `
        --filter $testFilter
    Assert-NativeCommandSucceeded "dotnet test"

    if ($RunFfmpegIntegrationTests) {
        if (-not $isWindowsPlatform) {
            throw "-RunFfmpegIntegrationTests is supported only by the Windows release job."
        }

        $prepareScript = Join-Path $repositoryRoot "scripts/prepare-ffmpeg-integration.ps1"
        $prepareOutput = @(& $prepareScript -EnvironmentFile "")
        if ($prepareOutput.Count -eq 0) {
            throw "The FFmpeg preparation script did not return a verified archive path."
        }

        $ffmpegArchivePath = [string]$prepareOutput[-1]
        if (-not (Test-Path -LiteralPath $ffmpegArchivePath -PathType Leaf)) {
            throw "The FFmpeg preparation script returned a missing archive: $ffmpegArchivePath"
        }

        $env:BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH = $ffmpegArchivePath
        $env:BILIBILI_RUN_FFMPEG_INTEGRATION_TESTS = "1"
        dotnet test BiliBiliLocalCacheManager.Playback.Tests/BiliBiliLocalCacheManager.Playback.Tests.csproj `
            --configuration Release `
            --no-build `
            --no-restore `
            --nologo `
            --filter "Category=FFmpegIntegration"
        Assert-NativeCommandSucceeded "real FFmpeg integration tests"
    }
}

$commonPublish = @(
    "--configuration", "Release",
    "--runtime", $runtimeIdentifier,
    "--self-contained", "true",
    "--nologo",
    "-p:Version=$Version",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

dotnet publish BiliBiliLocalCacheManager.Cli/BiliBiliLocalCacheManager.Cli.csproj `
    @commonPublish `
    --output $cliStage
Assert-NativeCommandSucceeded "dotnet publish CLI"

dotnet publish BiliBiliLocalCacheManager.Desktop.Host/BiliBiliLocalCacheManager.Desktop.Host.csproj `
    @commonPublish `
    --output $hostPublishPath
Assert-NativeCommandSucceeded "dotnet publish Desktop Host"

$cliExecutable = Join-Path $cliStage "BiliBiliLocalCacheManager.Cli$executableExtension"
$hostExecutable = Join-Path $hostPublishPath "BiliBiliLocalCacheManager.Desktop.Host$executableExtension"
Invoke-CliSmokeTest $cliExecutable
Invoke-HostSmokeTest $hostExecutable

Copy-Item -LiteralPath README.md, CHANGELOG.md, LICENSE -Destination $cliStage
Copy-Item -LiteralPath docs -Destination $cliStage -Recurse

Push-Location -LiteralPath $desktopPath
try {
    npm ci
    Assert-NativeCommandSucceeded "npm ci"

    if (-not $SkipTests) {
        npm run typecheck
        Assert-NativeCommandSucceeded "Electron typecheck"
        npm test
        Assert-NativeCommandSucceeded "Electron tests"
    }

    $previousHostPublishPath = $env:DESKTOP_HOST_PUBLISH_DIR
    try {
        $env:DESKTOP_HOST_PUBLISH_DIR = $hostPublishPath
        npm run $desktopTarget
        Assert-NativeCommandSucceeded "Electron $desktopTarget"
    }
    finally {
        $env:DESKTOP_HOST_PUBLISH_DIR = $previousHostPublishPath
    }
}
finally {
    Pop-Location
}

$packagedElectronExecutable = if ($isWindowsPlatform) {
    Join-Path $desktopReleasePath "win-unpacked/哔哩哔哩本地缓存管理器.exe"
}
else {
    Join-Path $desktopReleasePath "linux-unpacked/bilibili-local-cache-manager"
}
Assert-ElectronFuses $desktopPath $packagedElectronExecutable

if (-not $SkipElectronSmoke) {
    if ($isLinuxPlatform -and [string]::IsNullOrWhiteSpace($env:DISPLAY)) {
        throw "Electron smoke testing on Linux requires X11/XWayland. Run the script under xvfb-run or pass -SkipElectronSmoke explicitly."
    }
    Invoke-PackagedElectronSmokeTest $packagedElectronExecutable
}

$releaseFiles = New-Object System.Collections.Generic.List[string]
if ($isWindowsPlatform) {
    $cliArchive = Join-Path $outputPath "BiliBiliLocalCacheManager-cli-v$Version-win-x64.zip"
    Compress-Archive `
        -Path (Join-Path $cliStage "*") `
        -DestinationPath $cliArchive `
        -CompressionLevel Optimal
    Assert-NonEmptyFile $cliArchive
    $releaseFiles.Add($cliArchive)

    foreach ($extension in @("exe", "zip")) {
        $desktopPackagePath = Join-Path $desktopReleasePath "BiliBiliLocalCacheManager-$Version-windows-x64.$extension"
        Assert-NonEmptyFile $desktopPackagePath
        $copiedPackage = Join-Path $outputPath ([IO.Path]::GetFileName($desktopPackagePath))
        Copy-Item -LiteralPath $desktopPackagePath -Destination $copiedPackage
        $releaseFiles.Add($copiedPackage)
    }
}
else {
    $cliArchive = Join-Path $outputPath "BiliBiliLocalCacheManager-cli-v$Version-linux-x64.tar.gz"
    tar -czf $cliArchive -C $cliStage .
    Assert-NativeCommandSucceeded "tar CLI archive"
    Assert-NonEmptyFile $cliArchive
    $releaseFiles.Add($cliArchive)

    foreach ($extension in @("deb", "rpm")) {
        $desktopPackagePath = Join-Path $desktopReleasePath "BiliBiliLocalCacheManager-$Version-linux-x64.$extension"
        Assert-NonEmptyFile $desktopPackagePath
        $copiedPackage = Join-Path $outputPath ([IO.Path]::GetFileName($desktopPackagePath))
        Copy-Item -LiteralPath $desktopPackagePath -Destination $copiedPackage
        $releaseFiles.Add($copiedPackage)
    }
}

$checksumLines = foreach ($releaseFile in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($releaseFile))"
}
$checksumPath = Join-Path $outputPath "SHA256SUMS-$runtimeIdentifier.txt"
[IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

Write-Host "Release artifacts created for $runtimeIdentifier in $outputPath"
Get-ChildItem -LiteralPath $outputPath | Select-Object Name, Length
