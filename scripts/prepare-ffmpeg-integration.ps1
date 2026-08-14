[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$CacheDirectory,
    [string]$EnvironmentFile = $env:GITHUB_ENV,
    [ValidateRange(1, 10)][int]$RetryCount = 3,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot "..\ffmpeg-bundle.json"
}
if ([string]::IsNullOrWhiteSpace($CacheDirectory)) {
    $CacheDirectory = Join-Path $PSScriptRoot "..\.ci-cache\ffmpeg"
}

function Read-ValidatedManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "FFmpeg bundle manifest not found: $resolvedPath"
    }

    $manifest = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1) {
        throw "Unsupported FFmpeg bundle manifest schema: $($manifest.schemaVersion)"
    }
    if (-not [string]::Equals([string]$manifest.provider, "BtbN/FFmpeg-Builds", [StringComparison]::Ordinal)) {
        throw "Unsupported FFmpeg bundle provider: $($manifest.provider)"
    }

    $tag = [string]$manifest.tag
    $asset = [string]$manifest.asset
    $url = [string]$manifest.url
    $sha256 = ([string]$manifest.sha256).ToLowerInvariant()
    if ($tag -notmatch '^[0-9A-Za-z._-]+$') {
        throw "Invalid FFmpeg bundle tag: $tag"
    }
    if ($asset -notmatch '^[0-9A-Za-z._-]+$' -or
        -not [string]::Equals([IO.Path]::GetFileName($asset), $asset, [StringComparison]::Ordinal)) {
        throw "Invalid FFmpeg bundle asset: $asset"
    }

    $expectedUrl = "https://github.com/$($manifest.provider)/releases/download/$tag/$asset"
    if (-not [string]::Equals($url, $expectedUrl, [StringComparison]::Ordinal)) {
        throw "FFmpeg bundle URL does not match provider, tag, and asset."
    }
    if ($sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Invalid FFmpeg bundle SHA-256."
    }

    return [PSCustomObject]@{
        Tag = $tag
        Asset = $asset
        Url = $url
        Sha256 = $sha256
    }
}

$bundle = Read-ValidatedManifest -Path $ManifestPath
$resolvedCacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Path $resolvedCacheDirectory -Force | Out-Null
$archivePath = Join-Path $resolvedCacheDirectory $bundle.Asset

if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    $cachedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::Equals($cachedHash, $bundle.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}

if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
        $downloadPath = "$archivePath.download.$PID.$attempt"
        try {
            Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
            Invoke-WebRequest `
                -Uri $bundle.Url `
                -OutFile $downloadPath `
                -UseBasicParsing `
                -TimeoutSec $TimeoutSeconds

            $actualHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not [string]::Equals($actualHash, $bundle.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "FFmpeg SHA-256 mismatch. Expected $($bundle.Sha256), got $actualHash."
            }

            Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
            break
        }
        catch {
            Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
            if ($attempt -eq $RetryCount) {
                throw
            }

            Start-Sleep -Seconds ([int][Math]::Pow(2, $attempt - 1))
        }
    }
}

$verifiedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [string]::Equals($verifiedHash, $bundle.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Cached FFmpeg SHA-256 mismatch. Expected $($bundle.Sha256), got $verifiedHash."
}

if (-not [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    $resolvedEnvironmentFile = [IO.Path]::GetFullPath($EnvironmentFile)
    $environmentLines = @(
        "BILIBILI_LOCAL_CACHE_MANAGER_FFMPEG_ARCHIVE_PATH=$archivePath"
        "FFMPEG_BUNDLE_TAG=$($bundle.Tag)"
        "FFMPEG_BUNDLE_ASSET=$($bundle.Asset)"
        "FFMPEG_BUNDLE_SHA256=$($bundle.Sha256)"
    )
    [IO.File]::AppendAllLines(
        $resolvedEnvironmentFile,
        [string[]]$environmentLines,
        [Text.UTF8Encoding]::new($false))
}

Write-Output $archivePath
