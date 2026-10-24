#!/usr/bin/env bash
# Builds the vendored VulnVeto security-scanning engine (tools/vulnveto) so the
# AegisCode backend can run security scans without a separate VulnVeto checkout.
#
# Usage (from anywhere):
#   scripts/build-vulnveto-engine.sh
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
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ENGINE_DIR="$REPO_ROOT/tools/vulnveto"

if [ ! -f "$ENGINE_DIR/package.json" ]; then
  echo "ERROR: vendored engine not found at $ENGINE_DIR (expected tools/vulnveto/package.json). Are you in an AegisCode checkout?" >&2
  exit 1
fi

# --- Node.js check (20+ required by the engine's package.json "engines") ---
if ! command -v node >/dev/null 2>&1; then
  echo "ERROR: Node.js was not found on PATH. Install Node.js 20+ from https://nodejs.org and re-run this script." >&2
  exit 1
fi
NODE_VERSION="$(node --version | sed 's/^v//')"
NODE_MAJOR="${NODE_VERSION%%.*}"
if [ "$NODE_MAJOR" -lt 20 ]; then
  echo "ERROR: Node.js $NODE_VERSION is too old. The vendored engine requires Node.js 20+." >&2
  exit 1
fi
echo "Node.js $NODE_VERSION OK."

cd "$ENGINE_DIR"
echo "Installing pinned engine dependencies (npm ci) in $ENGINE_DIR ..."
npm ci

echo "Building the engine (npm run build) ..."
npm run build

# --- Verify the exact runtime files the sidecar harness imports ---
missing=()
for rel in \
  "dist/modules/dep-scan.js" \
  "dist/modules/secret-scan.js" \
  "dist/modules/package-risk.js" \
  "dist/modules/sbom.js" \
  "dist/ai/adapter.js" \
  "dist/config.js" \
  "package.json"; do
  if [ ! -f "$ENGINE_DIR/$rel" ]; then
    missing+=("$rel")
  fi
done
if [ "${#missing[@]}" -gt 0 ]; then
  echo "ERROR: engine build is incomplete. Missing: ${missing[*]}" >&2
  exit 1
fi

echo ""
echo "VulnVeto engine built and verified at $ENGINE_DIR"
echo "The AegisCode backend now resolves it automatically (no AEGIS_SECURITY_ENGINE_DIR needed)."
echo "Optional next step: npm test   (runs the engine test suite in $ENGINE_DIR)"
echo "Note: secret scanning needs gitleaks installed separately (see docs/SECURITY_ENGINE_INTEGRATION.md)."
