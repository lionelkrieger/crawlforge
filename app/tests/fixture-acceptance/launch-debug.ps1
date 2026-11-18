<#
.SYNOPSIS
  WP-08-04 fixture-acceptance debug launcher — a COPY of app\launch-crawlforge.ps1.
.DESCRIPTION
  Identical build steps and environment to the product's normal entry point
  (app\launch-crawlforge.ps1). The ONLY differences, sanctioned by the WP-08-04
  dispatch brief and modelled on audit-work/reports/runtime-probe-2/tools/launch-debug.ps1:
    1. -CdpPort parameter appends --remote-debugging-port=<port> to the Electron
       invocation so the harness can drive the real renderer over CDP.
    2. Electron is started with Start-Process (returns immediately) and its
       stdout/stderr are redirected into the harness results directory
       (-StdOutLog / -StdErrLog) instead of inheriting the console.
    3. The pinned local .NET 10 SDK/runtime is prepended to PATH (app/global.json
       pins 10.0.404; a .NET 11 preview is also installed on this machine).
    4. Duplicate-case proxy env vars are removed before Start-Process (Windows
       PowerShell env-block copy throws on HTTPS_PROXY + https_proxy duplicates).
  The original launch-crawlforge.ps1 is NEVER modified. Run with CF_AGENT_ID
  UNSET: the launcher hardcodes canonical bin\Debug paths and IS the product's
  normal entry point (sanctioned exception to builder CF_AGENT_ID isolation).
.PARAMETER Configuration
  The .NET build configuration (Debug or Release). Defaults to Debug.
.PARAMETER SkipBuild
  If set, refuse to launch when any component is missing or stale.
.PARAMETER CdpPort
  Electron remote debugging port (9222 or 9223). Defaults to 9222.
.PARAMETER StdOutLog / StdErrLog
  Redirect targets for the Electron process output (harness evidence dir).
#>

[CmdletBinding()]
param(
  [string] $Configuration = 'Debug',
  [switch] $SkipBuild,
  [switch] $Quiet,
  [int] $CdpPort = 9222,
  [string] $StdOutLog = '',
  [string] $StdErrLog = ''
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

$AppRoot       = 'E:\Crawlforge\app'
$FrontendDir   = Join-Path $AppRoot 'src\frontend'
$ElectronDir   = Join-Path $AppRoot 'src\electron'
$BackendDir    = Join-Path $AppRoot 'src\backend\CrawlForge.Host'
$BackendRoot   = Join-Path $AppRoot 'src\backend'
$VendorElectron = Join-Path $AppRoot 'packaging\vendor\electron'

$FrontendDist  = Join-Path $FrontendDir 'dist'
$ElectronDist  = Join-Path $ElectronDir 'dist'
$BackendDll    = Join-Path $BackendDir "bin\$Configuration\net10.0\CrawlForge.Host.dll"

$ElectronExe   = Join-Path $VendorElectron 'electron.exe'
# Fallback: the npm-installed binary in node_modules (same binary, different location).
$ElectronExeFallback = Join-Path $AppRoot 'node_modules\electron\dist\electron.exe'
$ElectronMain  = Join-Path $ElectronDist 'main.js'

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

$LogDir = Join-Path $env:LOCALAPPDATA 'CrawlForge\logs'
if (-not (Test-Path $LogDir)) {
  New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
}
$Timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$LogFile = Join-Path $LogDir "launch-$Timestamp.log"

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Write-Step([string] $msg) {
  Write-Host "`n[build] $msg" -ForegroundColor Cyan
}

function Test-File([string] $path) {
  return [System.IO.File]::Exists($path)
}

<#
  Scans source files and returns the newest LastWriteTime among them.
  Excludes bin, obj, dist, node_modules folders.
#>
function Get-NewestSource([string] $root, [string[]] $includePatterns) {
  $excludeDirs = @('bin', 'obj', 'dist', 'node_modules')
  $newest = [DateTime]::MinValue
  $newestFile = ''

  $allFiles = Get-ChildItem -Path $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object {
      $fullPath = $_.FullName
      $skip = $false
      foreach ($exc in $excludeDirs) {
        if ($fullPath -match "\\$exc\\") {
          $skip = $true
          break
        }
      }
      -not $skip
    }

  foreach ($file in $allFiles) {
    $matched = $false
    foreach ($pattern in $includePatterns) {
      if ($file.Name -like $pattern) {
        $matched = $true
        break
      }
    }
    if (-not $matched) {
      continue
    }
    if ($file.LastWriteTime -gt $newest) {
      $newest = $file.LastWriteTime
      $newestFile = $file.FullName
    }
  }

  if ($newestFile -eq '') {
    return $null
  }
  return @{ Time = $newest; File = $newestFile }
}

<#
  Checks whether a build output needs rebuilding.
  Returns $true if the output is missing or any source input is newer.
#>
function Test-NeedsBuild([string] $outputPath, [array] $sourceRoots, [string[]] $includePatterns, [string[]] $extraFiles) {
  # Missing output -> always needs build.
  if (-not (Test-File $outputPath)) {
    return @{ Needed = $true; Reason = 'missing'; NewestFile = '' }
  }

  $outputTime = (Get-Item $outputPath).LastWriteTime
  $newestTime = [DateTime]::MinValue
  $newestFile = ''

  # Scan each source root.
  foreach ($root in $sourceRoots) {
    if (-not (Test-Path $root)) {
      continue
    }
    $result = Get-NewestSource $root $includePatterns
    if ($result -ne $null) {
      if ($result.Time -gt $newestTime) {
        $newestTime = $result.Time
        $newestFile = $result.File
      }
    }
  }

  # Check extra individual files (e.g. Directory.Build.props).
  foreach ($extra in $extraFiles) {
    if (Test-File $extra) {
      $ft = (Get-Item $extra).LastWriteTime
      if ($ft -gt $newestTime) {
        $newestTime = $ft
        $newestFile = $extra
      }
    }
  }

  if ($newestFile -ne '' -and $newestTime -gt $outputTime) {
    return @{ Needed = $true; Reason = 'stale'; NewestFile = $newestFile }
  }

  return @{ Needed = $false; Reason = 'current'; NewestFile = '' }
}

<#
  Runs an external build command in a specified directory.
  Ensures Push-Location / Pop-Location are paired in a try/finally.
  Checks $LASTEXITCODE and throws with the component name on failure.
#>
function Invoke-BuildCommand([string] $workDir, [string] $command, [string] $component) {
  Push-Location $workDir
  try {
    Invoke-Expression $command
    if ($LASTEXITCODE -ne 0) {
      throw "$component build failed with exit code $LASTEXITCODE"
    }
  } finally {
    Pop-Location
  }
}

<#
  Shows a message box with the error message and log path.
  Only used when -Quiet is set (shortcut launched the window hidden).
#>
function Show-ErrorMessageBox([string] $message, [string] $logPath) {
  try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    $fullMessage = "$message`n`nLog: $logPath"
    [System.Windows.Forms.MessageBox]::Show($fullMessage, 'CrawlForge Launch Error', 'OK', 'Error') | Out-Null
  } catch {
    # Fallback: WScript.Shell Popup if Windows.Forms is unavailable.
    try {
      $shell = New-Object -ComObject WScript.Shell
      $shell.Popup("$message`n`nLog: $logPath", 0, 'CrawlForge Launch Error', 16) | Out-Null
    } catch {
      # Last resort: write to stderr.
      [Console]::Error.WriteLine($message)
    }
  }
}

# ---------------------------------------------------------------------------
# Main (wrapped for transcript + error handling)
# ---------------------------------------------------------------------------

$exitCode = 0
$failedStep = ''

try {
  Start-Transcript -Path $LogFile -Force | Out-Null
  try {

  # -------------------------------------------------------------------------
  # 1. Electron binary
  # -------------------------------------------------------------------------

  Write-Step 'Checking Electron binary'

  if (-not (Test-File $ElectronExe)) {
    if (Test-File $ElectronExeFallback) {
      Write-Host '  Copying from node_modules/electron/dist to vendor ...'
      New-Item -ItemType Directory -Force -Path $VendorElectron | Out-Null
      Copy-Item -Path (Join-Path $AppRoot 'node_modules\electron\dist\*') `
                -Destination $VendorElectron -Recurse -Force
    } else {
      throw @"
Electron binary not found at:
  $ElectronExe
  $ElectronExeFallback
Run 'node node_modules\electron\install.js' from $AppRoot first, then re-run this script.
"@
    }
  }
  Write-Host '  OK' -ForegroundColor Green

  # -------------------------------------------------------------------------
  # 2. .NET backend
  # -------------------------------------------------------------------------

  Write-Step 'Checking .NET backend'

  $backendPatterns = @('*.cs', '*.csproj', '*.props', '*.json')
  $backendExtra = @(
    (Join-Path $AppRoot 'Directory.Build.props'),
    (Join-Path $AppRoot 'Directory.Packages.props')
  )
  $backendCheck = Test-NeedsBuild -outputPath $BackendDll -sourceRoots @($BackendRoot) -includePatterns $backendPatterns -extraFiles $backendExtra

  if ($backendCheck.Needed) {
    if ($SkipBuild) {
      $msg = if ($backendCheck.Reason -eq 'missing') {
        "Backend DLL missing ($BackendDll). -SkipBuild refused."
      } else {
        "Backend stale (newest source: $($backendCheck.NewestFile)). -SkipBuild refused."
      }
      Write-Host "  WARNING: $msg" -ForegroundColor Yellow
      $failedStep = 'Backend'
      throw $msg
    }
    if ($backendCheck.Reason -eq 'missing') {
      Write-Host "  missing, building"
    } else {
      Write-Host "  stale (newest source: $($backendCheck.NewestFile)), building"
    }
    Invoke-BuildCommand -workDir $BackendDir -command "dotnet build $BackendDir -c $Configuration" -component 'Backend (dotnet build)'
  } else {
    Write-Host '  up to date'
  }
  Write-Host '  OK' -ForegroundColor Green

  # -------------------------------------------------------------------------
  # 3. Frontend bundle
  # -------------------------------------------------------------------------

  Write-Step 'Checking frontend bundle'

  $frontendIndex = Join-Path $FrontendDist 'index.html'
  $frontendPatterns = @('*')
  $frontendCheck = Test-NeedsBuild -outputPath $frontendIndex -sourceRoots @($FrontendDir) -includePatterns $frontendPatterns -extraFiles @()

  if ($frontendCheck.Needed) {
    if ($SkipBuild) {
      $msg = if ($frontendCheck.Reason -eq 'missing') {
        "Frontend dist missing ($frontendIndex). -SkipBuild refused."
      } else {
        "Frontend dist stale (newest source: $($frontendCheck.NewestFile)). -SkipBuild refused."
      }
      Write-Host "  WARNING: $msg" -ForegroundColor Yellow
      $failedStep = 'Frontend'
      throw $msg
    }
    if ($frontendCheck.Reason -eq 'missing') {
      Write-Host "  missing, building"
    } else {
      Write-Host "  stale (newest source: $($frontendCheck.NewestFile)), building"
    }
    Invoke-BuildCommand -workDir $FrontendDir -command 'npm run build' -component 'Frontend (npm run build)'
  } else {
    Write-Host '  up to date'
  }
  Write-Host '  OK' -ForegroundColor Green

  # -------------------------------------------------------------------------
  # 4. Electron shell
  # -------------------------------------------------------------------------

  Write-Step 'Checking Electron shell'

  $electronPatterns = @('*')
  $electronCheck = Test-NeedsBuild -outputPath $ElectronMain -sourceRoots @($ElectronDir) -includePatterns $electronPatterns -extraFiles @()

  if ($electronCheck.Needed) {
    if ($SkipBuild) {
      $msg = if ($electronCheck.Reason -eq 'missing') {
        "Electron shell missing ($ElectronMain). -SkipBuild refused."
      } else {
        "Electron shell stale (newest source: $($electronCheck.NewestFile)). -SkipBuild refused."
      }
      Write-Host "  WARNING: $msg" -ForegroundColor Yellow
      $failedStep = 'Electron shell'
      throw $msg
    }
    if ($electronCheck.Reason -eq 'missing') {
      Write-Host "  missing, building"
    } else {
      Write-Host "  stale (newest source: $($electronCheck.NewestFile)), building"
    }
    Invoke-BuildCommand -workDir $ElectronDir -command 'npx tsc -p tsconfig.build.json' -component 'Electron shell (tsc)'
  } else {
    Write-Host '  up to date'
  }
  Write-Host '  OK' -ForegroundColor Green

  # -------------------------------------------------------------------------
  # 5. Launch
  # -------------------------------------------------------------------------

  Write-Step 'Launching CrawlForge desktop shell'

  $env:CF_UI_ROOT = $FrontendDist
  $env:ELECTRON_OVERRIDE_DIST_PATH = $VendorElectron

  # CRITICAL: ELECTRON_RUN_AS_NODE makes electron.exe behave as plain Node.js
  # (no Electron APIs). Must be cleared for the desktop shell to work.
  Remove-Item Env:\ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue

  # Explicit backend command override so the supervisor spawns the correct
  # dotnet + DLL pair with a well-defined working directory.
  # NOTE: main.ts splits CF_BACKEND_ARGS on spaces, so the path must not
  # contain spaces or quotes. The CrawlForge path has none.
  $env:CF_BACKEND_COMMAND = 'dotnet'
  $env:CF_BACKEND_ARGS = $BackendDll

  $BackendCwd = Split-Path $BackendDll -Parent
  $env:CF_BACKEND_CWD = $BackendCwd

  Write-Host "  CF_UI_ROOT              = $env:CF_UI_ROOT"
  Write-Host "  ELECTRON_OVERRIDE_DIST  = $env:ELECTRON_OVERRIDE_DIST_PATH"
  Write-Host "  CF_BACKEND_COMMAND      = $env:CF_BACKEND_COMMAND"
  Write-Host "  CF_BACKEND_ARGS         = $env:CF_BACKEND_ARGS"
  Write-Host "  Electron entry          = $ElectronMain"
  Write-Host "  CDP port                = $CdpPort"
  Write-Host ""

  # Use vendor binary if present, otherwise fall back to node_modules.
  if (Test-File $ElectronExe) {
    $LaunchExe = $ElectronExe
  } else {
    $LaunchExe = $ElectronExeFallback
  }

  # WP-08-04 copy: use the pinned local .NET 10 SDK/runtime (app/global.json
  # 10.0.404) instead of the system .NET 11 RC install; mirrors probe-2 and
  # verify-packaging.ps1 policy.
  $env:PATH = 'C:\Users\lione\AppData\Local\Microsoft\dotnet;' + $env:PATH
  $env:DOTNET_ROOT = 'C:\Users\lione\AppData\Local\Microsoft\dotnet'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  $env:DOTNET_NOLOGO = '1'

  # Windows PowerShell Start-Process copies the env block case-insensitively;
  # this host has both HTTPS_PROXY and https_proxy, which otherwise throws.
  Remove-Item Env:\http_proxy,Env:\https_proxy,Env:\no_proxy -ErrorAction SilentlyContinue

  # WP-08-04 copy: Start-Process (non-blocking) + CDP port + evidence redirects.
  $LaunchArgs = @("--remote-debugging-port=$CdpPort", $ElectronMain)
  if ($StdOutLog -ne '' -and $StdErrLog -ne '') {
    Start-Process -FilePath $LaunchExe -ArgumentList $LaunchArgs -RedirectStandardOutput $StdOutLog -RedirectStandardError $StdErrLog
  } else {
    Start-Process -FilePath $LaunchExe -ArgumentList $LaunchArgs
  }

  }  # end inner try (transcript body)
  finally {
    Stop-Transcript | Out-Null
  }
}
catch {
  $exitCode = 1
  $errorMsg = $_.Exception.Message
  Write-Host ""
  Write-Host "[ERROR] $errorMsg" -ForegroundColor Red
  Write-Host "  Log: $LogFile" -ForegroundColor Gray
  if ($Quiet) {
    Show-ErrorMessageBox -message $errorMsg -logPath $LogFile
  }
}
finally {
  if ($exitCode -ne 0) {
    exit $exitCode
  }
}