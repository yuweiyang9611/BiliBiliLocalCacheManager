[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputRoot = "artifacts/release",
    [switch]$SkipTests,
    [switch]$RunFfmpegIntegrationTests
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

[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot "Directory.Build.props") -Raw
$declaredVersion = [string]$buildProperties.Project.PropertyGroup.VersionPrefix
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $declaredVersion
}

$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}
if (-not [string]::Equals($Version, $declaredVersion, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release version $Version does not match Directory.Build.props version $declaredVersion"
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
$outputIsArtifactsRoot = [string]::Equals($outputPath, $artifactsRoot, [StringComparison]::OrdinalIgnoreCase)
$outputIsInsideArtifacts = $outputPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)
if (-not ($outputIsArtifactsRoot -or $outputIsInsideArtifacts)) {
    throw "Output path must stay inside the repository artifacts directory: $outputPath"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

$stagingRoot = Join-Path $outputPath "staging"
$cliStage = Join-Path $stagingRoot "cli"
$wpfStage = Join-Path $stagingRoot "wpf"
New-Item -ItemType Directory -Path $cliStage, $wpfStage -Force | Out-Null

dotnet restore BiliBiliLocalCacheManager.slnx
Assert-NativeCommandSucceeded "dotnet restore"
if (-not $SkipTests) {
    dotnet test BiliBiliLocalCacheManager.slnx --configuration Release --no-restore --nologo --filter "Category!=UI&Category!=FFmpegIntegration"
    Assert-NativeCommandSucceeded "dotnet test"

    if ($RunFfmpegIntegrationTests) {
        $prepareScript = Join-Path $repositoryRoot "scripts\prepare-ffmpeg-integration.ps1"
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
            --no-restore `
            --no-build `
            --nologo `
            --filter "Category=FFmpegIntegration"
        Assert-NativeCommandSucceeded "real FFmpeg integration tests"
    }
}

function Invoke-PublishedCliSmokeTest {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published CLI executable is missing: $ExecutablePath"
    }

    $output = @(& $ExecutablePath --help 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Published CLI smoke test failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }
}

function Invoke-PublishedWpfSmokeTest {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$StateRoot
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published WPF executable is missing: $ExecutablePath"
    }

    if (Test-Path -LiteralPath $StateRoot) {
        Remove-Item -LiteralPath $StateRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($ExecutablePath)
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["BILIBILI_LOCAL_CACHE_MANAGER_TEST_MODE"] = "1"
    $startInfo.EnvironmentVariables["BILIBILI_LOCAL_CACHE_MANAGER_SETTINGS_PATH"] = Join-Path $StateRoot "settings.json"
    $startInfo.EnvironmentVariables["BILIBILI_LOCAL_CACHE_MANAGER_TRANSCODE_CACHE_ROOT"] = Join-Path $StateRoot "transcode"

    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) {
                throw "Published WPF executable exited early with code $($process.ExitCode)."
            }
        } while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)

        if ($process.MainWindowHandle -eq 0) {
            throw "Published WPF executable did not create a main window within 20 seconds."
        }

        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
            $process.Dispose()
        }

        if (Test-Path -LiteralPath $StateRoot) {
            Remove-Item -LiteralPath $StateRoot -Recurse -Force
        }
    }
}

function Assert-ArchiveContainsExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$ExecutableName
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = @($archive.Entries | Where-Object {
            [string]::Equals($_.FullName, $ExecutableName, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($entry.Count -ne 1 -or $entry[0].Length -le 0) {
            throw "Release archive does not contain one non-empty $ExecutableName entry: $ArchivePath"
        }
    }
    finally {
        $archive.Dispose()
    }
}

$commonPublish = @(
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--nologo",
    "-p:Version=$Version",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

dotnet publish BiliBiliLocalCacheManager.Cli/BiliBiliLocalCacheManager.Cli.csproj @commonPublish --output $cliStage
Assert-NativeCommandSucceeded "dotnet publish CLI"
dotnet publish BiliBiliLocalCacheManager.Wpf/BiliBiliLocalCacheManager.Wpf.csproj @commonPublish --output $wpfStage
Assert-NativeCommandSucceeded "dotnet publish WPF"

Invoke-PublishedCliSmokeTest (Join-Path $cliStage "BiliBiliLocalCacheManager.Cli.exe")
$wpfSmokeState = Join-Path $stagingRoot "wpf-smoke-state"
Invoke-PublishedWpfSmokeTest `
    (Join-Path $wpfStage "BiliBiliLocalCacheManager.Wpf.exe") `
    $wpfSmokeState

Copy-Item -LiteralPath README.md, CHANGELOG.md -Destination $cliStage
Copy-Item -LiteralPath README.md, CHANGELOG.md -Destination $wpfStage

$cliArchive = Join-Path $outputPath "BiliBiliLocalCacheManager-cli-v$Version-win-x64.zip"
$wpfArchive = Join-Path $outputPath "BiliBiliLocalCacheManager-wpf-v$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $cliStage "*") -DestinationPath $cliArchive -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $wpfStage "*") -DestinationPath $wpfArchive -CompressionLevel Optimal
Assert-ArchiveContainsExecutable $cliArchive "BiliBiliLocalCacheManager.Cli.exe"
Assert-ArchiveContainsExecutable $wpfArchive "BiliBiliLocalCacheManager.Wpf.exe"

$archives = @($cliArchive, $wpfArchive)
$checksumLines = foreach ($archive in $archives) {
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))"
}
$checksumPath = Join-Path $outputPath "SHA256SUMS.txt"
[IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

Write-Host "Release artifacts created in $outputPath"
Get-ChildItem -LiteralPath $outputPath | Select-Object Name, Length
