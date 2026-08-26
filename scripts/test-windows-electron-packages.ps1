[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PackageDirectory = "BiliBiliLocalCacheManager.Desktop/release"
)

$ErrorActionPreference = "Stop"
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "Windows Electron package smoke tests require Windows."
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Invalid package version: $Version"
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageDirectory))
$archivePath = Join-Path $packageRoot "BiliBiliLocalCacheManager-$Version-windows-x64.zip"
$installerPath = Join-Path $packageRoot "BiliBiliLocalCacheManager-$Version-windows-x64.exe"
foreach ($packagePath in @($archivePath, $installerPath)) {
    $item = Get-Item -LiteralPath $packagePath -ErrorAction Stop
    if ($item.PSIsContainer -or $item.Length -le 0) {
        throw "Expected a non-empty Windows desktop package: $packagePath"
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "blcm-windows-package-smoke-$([Guid]::NewGuid().ToString('N'))"
$portableRoot = Join-Path $testRoot "portable"
$installRoot = Join-Path $testRoot "installed"
$uninstallerPath = $null

function Invoke-PackagedSmoke {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Packaged Electron executable is missing: $ExecutablePath"
    }

    $previousHostOverride = $env:CACHE_MANAGER_HOST_PATH
    $previousDotnetOverride = $env:CACHE_MANAGER_DOTNET_PATH
    try {
        $env:CACHE_MANAGER_HOST_PATH = Join-Path $testRoot "must-not-run-host.exe"
        $env:CACHE_MANAGER_DOTNET_PATH = Join-Path $testRoot "must-not-run-dotnet.exe"
        $startParameters = @{
            FilePath = $ExecutablePath
            ArgumentList = "--smoke-test"
            WindowStyle = "Hidden"
            Wait = $true
            PassThru = $true
        }
        $process = Start-Process @startParameters
        if ($process.ExitCode -ne 0) {
            throw "Packaged Electron smoke failed with exit code $($process.ExitCode): $ExecutablePath"
        }
    }
    finally {
        $env:CACHE_MANAGER_HOST_PATH = $previousHostOverride
        $env:CACHE_MANAGER_DOTNET_PATH = $previousDotnetOverride
    }
}

try {
    New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $portableRoot
    Invoke-PackagedSmoke (Join-Path $portableRoot "哔哩哔哩本地缓存管理器.exe")

    $installParameters = @{
        FilePath = $installerPath
        ArgumentList = "/S /D=$installRoot"
        WindowStyle = "Hidden"
        Wait = $true
        PassThru = $true
    }
    $installer = Start-Process @installParameters
    if ($installer.ExitCode -ne 0) {
        throw "NSIS silent install failed with exit code $($installer.ExitCode)."
    }

    $uninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter "Uninstall*.exe" -File)
    if ($uninstallers.Count -ne 1) {
        throw "Expected exactly one NSIS uninstaller, found $($uninstallers.Count)."
    }
    $uninstallerPath = $uninstallers[0].FullName
    Invoke-PackagedSmoke (Join-Path $installRoot "哔哩哔哩本地缓存管理器.exe")
}
finally {
    if (-not $uninstallerPath -and (Test-Path -LiteralPath $installRoot -PathType Container)) {
        $cleanupUninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter "Uninstall*.exe" -File)
        if ($cleanupUninstallers.Count -eq 1) {
            $uninstallerPath = $cleanupUninstallers[0].FullName
        }
    }
    if ($uninstallerPath -and (Test-Path -LiteralPath $uninstallerPath -PathType Leaf)) {
        $uninstallParameters = @{
            FilePath = $uninstallerPath
            ArgumentList = "/S"
            WindowStyle = "Hidden"
            Wait = $true
            PassThru = $true
        }
        $uninstaller = Start-Process @uninstallParameters
        if ($uninstaller.ExitCode -ne 0) {
            Write-Warning "NSIS silent uninstall returned exit code $($uninstaller.ExitCode)."
        }
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove package smoke directory outside the temporary root: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host "Windows Electron zip extraction and NSIS installation smoke tests passed."
