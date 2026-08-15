[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & git -C $PSScriptRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git was not found. Install Git and make sure it is available on PATH."
}

$repositoryRoot = (Invoke-Git -Arguments @("rev-parse", "--show-toplevel") | Select-Object -First 1).Trim()
$expectedRoot = [System.IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
$actualRoot = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\', '/')

if (-not [string]::Equals($expectedRoot, $actualRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "This script must remain in the repository root. Expected '$expectedRoot', but Git reported '$actualRoot'."
}

$prePushHook = Join-Path $PSScriptRoot ".githooks\pre-push"
if (-not (Test-Path -LiteralPath $prePushHook -PathType Leaf)) {
    throw "The required hook was not found: $prePushHook"
}

Invoke-Git -Arguments @("config", "--local", "core.hooksPath", ".githooks") | Out-Null
$configuredHooksPath = (Invoke-Git -Arguments @("config", "--local", "--get", "core.hooksPath") | Select-Object -First 1).Trim()

if ($configuredHooksPath -ne ".githooks") {
    throw "Git hooks were not enabled. core.hooksPath is '$configuredHooksPath'."
}

Write-Host "Git hooks are enabled for this clone: core.hooksPath=.githooks" -ForegroundColor Green

$emailOutput = & git -C $PSScriptRoot config --get user.email 2>$null
$configuredEmail = if ($LASTEXITCODE -eq 0) { ($emailOutput | Select-Object -First 1).Trim() } else { "" }
$isGitHubNoreply = $configuredEmail -match '^(noreply@github\.com|[^@]+@users\.noreply\.github\.com)$'

if ($isGitHubNoreply) {
    Write-Host "The effective Git email is a GitHub noreply address." -ForegroundColor Green
}
else {
    Write-Warning "The effective Git email is missing or is not a GitHub noreply address. The pre-push hook will reject commits created with it. See GIT_HOOKS.md before committing."
}
