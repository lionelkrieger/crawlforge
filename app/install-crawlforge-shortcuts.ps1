<#
.SYNOPSIS
  Installs desktop and Start Menu shortcuts for CrawlForge.
.DESCRIPTION
  Creates "CrawlForge.lnk" shortcuts on the current user's Desktop and in the
  current user's Start Menu Programs folder. Each shortcut launches
  launch-crawlforge.ps1 via powershell.exe with a hidden window and -Quiet flag.
  Safe to re-run (overwrites existing shortcuts). Prints the paths created.
  Designed for Windows PowerShell 5.1.
#>

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

$AppRoot        = 'E:\Crawlforge\app'
$LaunchScript   = Join-Path $AppRoot 'launch-crawlforge.ps1'
$ShortcutName   = 'CrawlForge.lnk'

# Icon: prefer vendored electron.exe, fall back to node_modules.
$VendorExe = Join-Path $AppRoot 'packaging\vendor\electron\electron.exe'
$NodeModulesExe = Join-Path $AppRoot 'node_modules\electron\dist\electron.exe'
if (Test-Path $VendorExe) {
  $IconPath = $VendorExe
} elseif (Test-Path $NodeModulesExe) {
  $IconPath = $NodeModulesExe
} else {
  $IconPath = ''
}

# Shortcut target locations (current user).
$DesktopPath = [Environment]::GetFolderPath('Desktop')
$StartMenuPrograms = [Environment]::GetFolderPath('Programs')

$ShortcutTargets = @(
  @{ Label = 'Desktop';   Path = Join-Path $DesktopPath $ShortcutName }
  @{ Label = 'Start Menu'; Path = Join-Path $StartMenuPrograms $ShortcutName }
)

# ---------------------------------------------------------------------------
# Create shortcuts
# ---------------------------------------------------------------------------

$shell = New-Object -ComObject WScript.Shell

$arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Quiet' -f $LaunchScript

foreach ($target in $ShortcutTargets) {
  $lnkPath = $target.Path

  # Remove existing shortcut so we always write a fresh one.
  if (Test-Path $lnkPath) {
    Remove-Item $lnkPath -Force
  }

  $shortcut = $shell.CreateShortcut($lnkPath)
  $shortcut.TargetPath = 'powershell.exe'
  $shortcut.Arguments = $arguments
  $shortcut.WorkingDirectory = $AppRoot
  $shortcut.Description = 'Launch CrawlForge desktop application'
  $shortcut.WindowStyle = 7  # minimized (hidden via -WindowStyle Hidden)

  if ($IconPath -ne '' -and (Test-Path $IconPath)) {
    $shortcut.IconLocation = $IconPath
  }

  $shortcut.Save()

  Write-Host ""
  Write-Host ("[{0}] Created: {1}" -f $target.Label, $lnkPath) -ForegroundColor Green
  Write-Host ("  TargetPath : {0}" -f $shortcut.TargetPath)
  Write-Host ("  Arguments  : {0}" -f $shortcut.Arguments)
  Write-Host ("  WorkingDir : {0}" -f $shortcut.WorkingDirectory)
  if ($IconPath -ne '' -and (Test-Path $IconPath)) {
    Write-Host ("  Icon       : {0}" -f $IconPath)
  }
}

Write-Host ""
Write-Host "Done. Both shortcuts created." -ForegroundColor Cyan
