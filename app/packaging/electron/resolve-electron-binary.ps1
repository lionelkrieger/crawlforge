# WP-07-04 Electron shell binary resolution — LOCAL ONLY, no runtime fetch.
#
# The npm `electron` wrapper's postinstall (binary download from GitHub
# releases) is blocked by the environment's allowScripts=false policy, and a
# runtime fetch would violate the offline-first invariant anyway. The wrapper
# (node_modules/electron/index.js) natively honors ELECTRON_OVERRIDE_DIST_PATH
# and path.txt; this resolver configures a LOCAL dist through those channels:
#
#   1. override-env   — ELECTRON_OVERRIDE_DIST_PATH already points at a local
#                       Electron dist containing electron.exe.
#   2. bundled-vendor — packaging/vendor/electron/electron.exe (user-provided
#                       local dist dropped into the packaging tree).
#   3. system-path    — electron.exe discoverable on PATH (local install).
#   4. not-bundled    — no local dist present. The shell's unit suites run on
#                       fakes (no binary required); desktop launch requires a
#                       user-provided local dist via mode 1/2. NO download is
#                       attempted or permitted.
#
# Feed policy: nuget.org / npm registry only. The Electron dist zip is NOT
# hosted on the npm registry (the wrapper fetches GitHub releases), so
# bundling a dist is a product-owner-approved binary-source decision —
# recorded in the sign-off report, never silently resolved.
#
# Emits a JSON object: { mode, path, noRuntimeFetch, detail }.

$ErrorActionPreference = 'Stop'
$packagingRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Split-Path -Parent $packagingRoot

function Find-ElectronExe([string]$distRoot) {
    if ([string]::IsNullOrWhiteSpace($distRoot)) { return $null }
    $candidate = Join-Path $distRoot 'electron.exe'
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    return $null
}

$mode = 'not-bundled'
$path = $null
$detail = ''

# 1. Environment override (the wrapper's own local-dist channel).
$override = $env:ELECTRON_OVERRIDE_DIST_PATH
$found = Find-ElectronExe $override
if ($found) {
    $mode = 'override-env'
    $path = $found
    $detail = 'ELECTRON_OVERRIDE_DIST_PATH points at a local Electron dist.'
}

# 2. Vendored local dist inside the packaging tree.
if (-not $path) {
    $found = Find-ElectronExe (Join-Path $packagingRoot 'vendor\electron')
    if ($found) {
        $mode = 'bundled-vendor'
        $path = $found
        $detail = 'Local dist bundled at packaging/vendor/electron; launcher sets ELECTRON_OVERRIDE_DIST_PATH to it.'
    }
}

# 3. Local install on PATH.
if (-not $path) {
    $command = Get-Command 'electron.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $mode = 'system-path'
        $path = $command.Source
        $detail = 'Local Electron installation found on PATH.'
    }
}

if (-not $path) {
    $detail = 'No local Electron dist present. Shell unit suites run on fakes (no binary needed); desktop launch requires a user-provided local dist (ELECTRON_OVERRIDE_DIST_PATH or packaging/vendor/electron). No download attempted.'
}

# Verify the wrapper's download channel stays closed: the blocked postinstall
# left no dist, and nothing in this resolution triggers install.js.
$wrapperDist = Join-Path $appRoot 'node_modules\electron\dist'
$wrapperDownloaded = Test-Path $wrapperDist

return [pscustomobject]@{
    mode               = $mode
    path               = $path
    noRuntimeFetch     = $true
    wrapperDistPresent = $wrapperDownloaded
    detail             = $detail
}
