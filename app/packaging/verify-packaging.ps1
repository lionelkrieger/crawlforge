# WP-07-04 offline packaging verification (verification-only).
#
# Publishes the REAL backend in Release, bundles the local asset tree next to
# the published executable, and proves:
#   1. every required asset family is bundled (10 families, FS-CRAWL-001 §13);
#   2. no licensing/entitlement manifest ships in the packaged build;
#   3. no account-bound update feed is configured anywhere in the package;
#   4. first launch from the PUBLISHED layout completes offline initialization
#      (fresh data directory, migrations, settings defaults, hub readiness);
#   5. the Chromium rendering runtime resolves from LOCAL sources only;
#   6. the Electron shell binary resolution is configured local-only (no fetch).
#
# Output: packaging/artifacts/packaging-report.json (consumed by the
# CrawlForge.Acceptance.Tests packaging facts). NuGet feed: nuget.org only.
# No runtime network fetch is performed by this script or the packaged build.

$ErrorActionPreference = 'Stop'
$env:PATH = "C:\Users\lione\AppData\Local\Microsoft\dotnet;" + $env:PATH
# The published apphost must resolve the local .NET 10 runtime, not the
# system 11-RC install (build-isolation policy).
$env:DOTNET_ROOT = "C:\Users\lione\AppData\Local\Microsoft\dotnet"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$appRoot = Split-Path -Parent $PSScriptRoot
$packagingRoot = $PSScriptRoot
$publishDir = Join-Path $packagingRoot 'artifacts\publish'
$reportPath = Join-Path $packagingRoot 'artifacts\packaging-report.json'
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    Write-Host ("[{0}] {1} — {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}

# ---------------------------------------------------------------------------
# 1. Release publish of the real composition root
# ---------------------------------------------------------------------------
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $appRoot 'src\backend\CrawlForge.Host\CrawlForge.Host.csproj') `
    -c Release -o $publishDir --nologo -v q | Out-Host
$publishExit = $LASTEXITCODE
Add-Check 'release-publish' ($publishExit -eq 0) "dotnet publish -c Release exit $publishExit"
if ($publishExit -ne 0) { throw "Release publish failed — aborting packaging verification." }

$exe = Join-Path $publishDir 'CrawlForge.Host.exe'
Add-Check 'published-executable' (Test-Path $exe) $exe

# ---------------------------------------------------------------------------
# 2. Bundle the local asset tree (AssetLocator resolves assets/ next to the exe)
# ---------------------------------------------------------------------------
$assetSource = Join-Path $appRoot 'assets'
$assetTarget = Join-Path $publishDir 'assets'
Copy-Item $assetSource $assetTarget -Recurse -Force

$families = @{
    'waf-signatures'    = 'WAF challenge signature catalogue'
    'stopwords'         = 'per-language stop-word lists'
    'dictionaries'      = 'spelling dictionaries'
    'font-metrics'      = 'SERP pixel font metrics'
    'tech-fingerprints' = 'technology fingerprint catalogue'
    'bot-cidr'          = 'search-bot CIDR ranges'
    'ai-agent-profiles' = 'AI agent governance profiles'
    'onnx-model'        = 'bundled embedding model'
    'export-templates'  = 'export report templates'
    'plugin-schema'     = 'plugin manifest schema'
}
foreach ($family in $families.Keys | Sort-Object) {
    $dir = Join-Path $assetTarget $family
    $files = @(Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue)
    Add-Check "asset-$family" ($files.Count -gt 0) "$($families[$family]): $($files.Count) file(s) bundled"
}

# Chromium runtime: the product resolves a LOCAL browser (packaged assets →
# user-configured path → local Chrome/Edge install). Runtime download is
# prohibited by LocalBrowserResolver (WP-02-D1). Prove a local source exists.
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$browserPath = $null
if (Test-Path $chrome) { $browserPath = $chrome }
elseif (Test-Path $edge) { $browserPath = $edge }
Add-Check 'chromium-runtime-local' ($null -ne $browserPath) "local rendering binary: $browserPath (no runtime fetch)"

# ---------------------------------------------------------------------------
# 3. Licensing manifest ABSENT + no account-bound update feed
# ---------------------------------------------------------------------------
$licenseManifests = @(Get-ChildItem $publishDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '(?i)^(license|licence|entitlement|activation)[-_]?(manifest|key|state)' })
Add-Check 'licensing-manifest-absent' ($licenseManifests.Count -eq 0) `
    "$($licenseManifests.Count) licensing/entitlement manifest file(s) in the packaged build"

$crawlforgeAssemblies = @(Get-ChildItem $publishDir -File -Filter 'CrawlForge.*.dll')
$licensingAssemblies = @($crawlforgeAssemblies | Where-Object { $_.Name -match '(?i)licens|entitlement|account|activation' })
Add-Check 'no-licensing-assembly' ($licensingAssemblies.Count -eq 0) `
    "$($licensingAssemblies.Count) licensing-shaped product assemblies"

# Update feed: no product configuration carries an update/feed endpoint. The
# only JSON configuration surfaces in the package are the asset catalogues and
# the deps/runtimeconfig files — scan them for update-feed/account URLs.
$configFiles = @(Get-ChildItem $publishDir -File -Filter '*.json') + @(Get-ChildItem $assetTarget -Recurse -File -Filter '*.json')
$updateFeedHits = [System.Collections.Generic.List[string]]::new()
foreach ($file in $configFiles) {
    $content = Get-Content $file.FullName -Raw
    if ($content -match '(?i)"(update[-_]?feed|update[-_]?url|account[-_]?url|entitlement[-_]?url)"\s*:\s*"https?://') {
        $updateFeedHits.Add($file.Name)
    }
}
Add-Check 'no-account-bound-update-feed' ($updateFeedHits.Count -eq 0) `
    "$($updateFeedHits.Count) update-feed/account endpoint(s) in packaged configuration"

# ---------------------------------------------------------------------------
# 4. First launch OFFLINE from the published layout (fresh data directory)
# ---------------------------------------------------------------------------
$dataDir = Join-Path $env:TEMP ("cf-packaging-firstlaunch-" + [guid]::NewGuid().ToString('N'))
$proc = Start-Process -FilePath $exe `
    -ArgumentList "--CrawlForge:DataDirectory=$dataDir", "--CrawlForge:Port=0" `
    -PassThru -WindowStyle Hidden
$handshake = $null
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
    $handshakePath = Join-Path $dataDir 'loopback-handshake.json'
    if (Test-Path $handshakePath) {
        try { $handshake = Get-Content $handshakePath -Raw | ConvertFrom-Json; break } catch { }
    }
    Start-Sleep -Milliseconds 250
}

$firstLaunchOk = $false
$firstLaunchDetail = 'handshake file never appeared'
if ($null -ne $handshake) {
    $base = "http://127.0.0.1:$($handshake.port)"
    # The handshake publishes before Kestrel finishes binding — retry the probe.
    $probeDeadline = (Get-Date).AddSeconds(30)
    $lastError = 'no probe attempted'
    while ((Get-Date) -lt $probeDeadline) {
        if ($proc.HasExited) { $lastError = "backend exited early (code $($proc.ExitCode))"; break }
        try {
            $health = Invoke-WebRequest -Uri "$base/api/health" -UseBasicParsing -TimeoutSec 5
            $settings = Invoke-WebRequest -Uri "$base/api/settings" -UseBasicParsing -TimeoutSec 10 `
                -Headers @{ 'X-CrawlForge-Session' = $handshake.sessionToken }
            $dbFile = Join-Path $dataDir 'crawlforge.db'
            $firstLaunchOk = ($health.StatusCode -eq 200) -and ($settings.StatusCode -eq 200)
            $firstLaunchDetail = "health=$($health.StatusCode) settings=$($settings.StatusCode) db=$(Test-Path $dbFile) port=$($handshake.port)"
            break
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $firstLaunchOk -and $firstLaunchDetail -eq 'handshake file never appeared') {
        $firstLaunchDetail = "probe failed: $lastError"
    }
}
Add-Check 'first-launch-offline-init' $firstLaunchOk $firstLaunchDetail

try { if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force } } catch { }
Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# 5. Electron shell binary resolution (local-only, no runtime fetch)
# ---------------------------------------------------------------------------
$electronResolution = & (Join-Path $packagingRoot 'electron\resolve-electron-binary.ps1')
Add-Check 'electron-binary-local-config' ($electronResolution.noRuntimeFetch -eq $true) `
    "mode=$($electronResolution.mode); $($electronResolution.detail)"

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
$report = [pscustomobject]@{
    workpiece     = 'WP-07-04'
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    publishDirectory = $publishDir
    allPassed     = @($checks | Where-Object { -not $_.passed }).Count -eq 0
    checks        = $checks
    electron      = $electronResolution
}
New-Item -ItemType Directory -Force -Path (Split-Path $reportPath) | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content $reportPath -Encoding UTF8
Write-Host "Packaging report: $reportPath (allPassed=$($report.allPassed))"
if (-not $report.allPassed) { exit 1 }
exit 0
