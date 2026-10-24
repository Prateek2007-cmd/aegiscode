# Builds the vendored VulnVeto security-scanning engine (tools/vulnveto) so the
# AegisCode backend can run security scans without a separate VulnVeto checkout.
#
# Usage (from anywhere):
#   scripts\Build-VulnvetoEngine.ps1
#
# What it does:
#   1. Verifies Node.js 20+ is on PATH.
#   2. Installs the engine's pinned dev dependencies from its lockfile (npm ci).
#   3. Builds the engine with its real build process (tsc -> dist/).
#   4. Verifies every runtime file the C# sidecar harness imports actually exists.
#
# It never installs global packages and never touches node_modules of any other
# project. Requires: Node.js 20+ (https://nodejs.org), npm on PATH.
# No secrets are read or written by this script.

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$EngineDir = Join-Path $RepoRoot 'tools\vulnveto'

if (-not (Test-Path (Join-Path $EngineDir 'package.json'))) {
    Write-Error "Vendored engine not found at $EngineDir (expected tools\vulnveto\package.json). Are you in an AegisCode checkout?"
    exit 1
}

# --- Node.js check (20+ required by the engine's package.json "engines") ---
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) {
    Write-Error "Node.js was not found on PATH. Install Node.js 20+ from https://nodejs.org, restart PowerShell, and re-run this script."
    exit 1
}
$nodeVersion = (node --version).TrimStart('v')
$major = [int]($nodeVersion.Split('.')[0])
if ($major -lt 20) {
    Write-Error "Node.js $nodeVersion is too old. The vendored engine requires Node.js 20+."
    exit 1
}
Write-Host "Node.js $nodeVersion OK."

Push-Location $EngineDir
try {
    Write-Host "Installing pinned engine dependencies (npm ci) in $EngineDir ..."
    npm ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit $LASTEXITCODE). Check your network connection and that tools\vulnveto\package-lock.json matches package.json." }

    Write-Host "Building the engine (npm run build) ..."
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)." }

    # --- Verify the exact runtime files the sidecar harness imports ---
    $required = @(
        'dist\modules\dep-scan.js',
        'dist\modules\secret-scan.js',
        'dist\modules\package-risk.js',
        'dist\modules\sbom.js',
        'dist\ai\adapter.js',
        'dist\config.js',
        'package.json'
    )
    $missing = @()
    foreach ($rel in $required) {
        if (-not (Test-Path (Join-Path $EngineDir $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) {
        Write-Error ("Engine build is incomplete. Missing: " + ($missing -join ', '))
        exit 1
    }

    Write-Host ""
    Write-Host "VulnVeto engine built and verified at $EngineDir"
    Write-Host "The AegisCode backend now resolves it automatically (no AEGIS_SECURITY_ENGINE_DIR needed)."
    Write-Host "Optional next step: npm test   (runs the engine test suite in $EngineDir)"
    Write-Host "Note: secret scanning needs gitleaks installed separately (see docs/SECURITY_ENGINE_INTEGRATION.md)."
}
finally {
    Pop-Location
}
