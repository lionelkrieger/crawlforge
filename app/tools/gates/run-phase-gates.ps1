<#
.SYNOPSIS
    CrawlForge orchestrator phase gates (WP-08-05).

.DESCRIPTION
    Executes a phase's orchestrator_gates exactly as declared in
    buildplan/phases/<phase>-*.yaml: after_each_wave first, then every
    after_waves entry in declaration order. Fail-fast: the first non-zero
    exit stops the run. Every command's full output is logged under
    app/tools/gates/logs/<phase>-<timestamp>/.

    Canonical surface only: aborts if CF_AGENT_ID is set (gates run on the
    canonical build surface; agent-scoped builds are for builders, not gates).

    Pinned SDK: dotnet commands run with working directory app/ so that
    app/global.json (10.0.404, rollForward latestFeature) governs SDK
    resolution; the script verifies `dotnet --version` is 10.0.x before
    running anything and never modifies global.json.

    KnownGap interpretation (phase-08 ONLY): AGENT-INSTRUCTIONS.md §12
    ("The known REST counter-key mismatch ... is tracked as a KnownGap test
    owned by WP-09-06; phase-08 gates exclude exactly that gap, every later
    gate runs the full suite"). The phase-08 after_each_wave list contains an
    unfiltered `dotnet test CrawlForge.sln`; once WP-08-05 wires the
    conformance project into the solution, that command would include the
    single KnownGap failing test. This script therefore appends
    --filter "KnownGap!=WP-09-06" to every phase-08 `dotnet test` command that
    does not already carry a --filter. Every such transformation is logged.
    Phases 09+ run their commands verbatim (full suite; WP-09-06 owns the fix).

    check_traceability.py side effect: the script rewrites
    architecture/TRACEABILITY.md with CRLF line endings (content identical to
    the committed LF version). After that gate passes, this script restores
    the committed file IF AND ONLY IF the diff is CRLF-only
    (git diff --ignore-cr-at-eol is empty). A content diff is never discarded.

.PARAMETER Phase
    Phase id to gate (default phase-08). Must match a buildplan/phases file.

.PARAMETER ListOnly
    Print the effective commands (with working directories and any KnownGap
    transformation) without executing them.

.PARAMETER ValidateAll
    Resolve every orchestrator_gates command in phase-08..phase-16: parse the
    YAML, check each referenced path exists, and report. Phase-08 must fully
    resolve (exit 1 otherwise); later phases report missing targets as INFO
    (their workpieces are not built yet — commands are validated, not run).
    This is the WP-08-05 contract_tests self-test ("every orchestrator_gates
    command in phase-08..phase-15 resolves"); nothing is executed.

.PARAMETER SelfTest
    In-process assertions on the gate planning logic (no gates executed):
    phase-08 unfiltered dotnet test gains the KnownGap filter; already-filtered
    commands are untouched; phase-09 commands are never transformed; dotnet
    commands run from app/ and python from the repo root; the CF_AGENT_ID
    guard aborts a child invocation. Exit 0 = all assertions pass.

.EXAMPLE
    pwsh -NoProfile -File app\tools\gates\run-phase-gates.ps1 -Phase phase-08
    pwsh -NoProfile -File app\tools\gates\run-phase-gates.ps1 -ListOnly
    pwsh -NoProfile -File app\tools\gates\run-phase-gates.ps1 -ValidateAll
#>
[CmdletBinding()]
param(
    [string]$Phase = "phase-08",
    [switch]$ListOnly,
    [switch]$ValidateAll,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$script:GateRoot = $PSScriptRoot                                   # app/tools/gates
$script:AppRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$script:RepoRoot = (Resolve-Path (Join-Path $AppRoot "..")).Path

function Write-GateLog([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Message
    Write-Host $line
    if ($script:SummaryPath) { Add-Content -LiteralPath $script:SummaryPath -Value $line -Encoding utf8 }
}

function Fail-Gate([string]$Message) {
    Write-GateLog "FAIL: $Message"
    exit 1
}

# --- prerequisites -----------------------------------------------------------

if ($env:CF_AGENT_ID) {
    Fail-Gate "CF_AGENT_ID='$($env:CF_AGENT_ID)' is set. Orchestrator gates run on the CANONICAL surface (CF_AGENT_ID unset). Unset it and re-run."
}

try { $null = python -c "import yaml" 2>&1 } catch { }
if ($LASTEXITCODE -ne 0) { Fail-Gate "Python + PyYAML are required to parse the phase YAML (python -c 'import yaml' failed). No installs are performed by this script." }

$phaseFile = Get-ChildItem (Join-Path $script:RepoRoot "buildplan\phases") -Filter "$Phase-*.yaml" | Select-Object -First 1
if (-not $phaseFile) { Fail-Gate "No buildplan/phases file matches $Phase." }

# Parse orchestrator_gates via Python + PyYAML (read-only; the plan is never edited).
$parseScript = @'
import json, sys, yaml
d = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
g = d.get("orchestrator_gates") or {}
out = {"phase_id": d["phase_id"], "after_each_wave": list(g.get("after_each_wave") or []),
       "after_waves": [{"wave": w, "commands": list(c or [])} for w, c in (g.get("after_waves") or {}).items()]}
print(json.dumps(out))
'@
$gatesJson = python -c $parseScript $phaseFile.FullName
if ($LASTEXITCODE -ne 0) { Fail-Gate "Failed to parse orchestrator_gates from $($phaseFile.Name)." }
$gates = $gatesJson | ConvertFrom-Json

# --- command planning --------------------------------------------------------

function Get-EffectiveCommands($Gates, [string]$PhaseId) {
    $plan = @()
    $index = 0
    foreach ($cmd in $Gates.after_each_wave) {
        $index++
        $plan += [pscustomobject]@{ Index = $index; Wave = "after_each_wave"; Raw = $cmd; Effective = $cmd; Transformed = $false }
    }
    foreach ($entry in $Gates.after_waves) {
        foreach ($cmd in $entry.commands) {
            $index++
            $plan += [pscustomobject]@{ Index = $index; Wave = $entry.wave; Raw = $cmd; Effective = $cmd; Transformed = $false }
        }
    }
    # KnownGap exclusion: phase-08 only, unfiltered dotnet test only (see header).
    if ($PhaseId -eq "phase-08") {
        foreach ($item in $plan) {
            if ($item.Effective -match '^\s*dotnet\s+test\b' -and $item.Effective -notmatch '--filter') {
                $item.Effective = '{0} --filter "KnownGap!=WP-09-06"' -f $item.Effective
                $item.Transformed = $true
            }
        }
    }
    return $plan
}

function Get-CommandWorkdir([string]$Cmd) {
    # dotnet resolves the SDK from the WORKING DIRECTORY (global.json walk-up),
    # not from the project path — always run dotnet from app/.
    if ($Cmd -match '^\s*dotnet\b') { return $script:AppRoot }
    # Everything else (python register checks, fixture harness, cd && npm/npx)
    # runs from the repo root; cd-prefixed commands set their own directory.
    return $script:RepoRoot
}

$plan = Get-EffectiveCommands $gates $Phase

# --- SelfTest: planning-logic assertions (no gates executed) ------------------

if ($SelfTest) {
    $failures = @()
    function Assert-True([bool]$Condition, [string]$Name) {
        if ($Condition) { Write-Host "PASS $Name" } else { Write-Host "FAIL $Name"; $script:SelfTestFailures += $Name }
    }
    $script:SelfTestFailures = @()

    # 1. phase-08: unfiltered sln test gains exactly the KnownGap filter.
    $slnItem = $plan | Where-Object { $_.Raw -match '^\s*dotnet\s+test\b.*CrawlForge\.sln' } | Select-Object -First 1
    Assert-True ($null -ne $slnItem -and $slnItem.Transformed -and $slnItem.Effective -eq ('{0} --filter "KnownGap!=WP-09-06"' -f $slnItem.Raw)) "phase-08 unfiltered sln test gains KnownGap filter"

    # 2. phase-08: already-filtered conformance command is untouched.
    $confItem = $plan | Where-Object { $_.Raw -match 'ContractConformance\.Tests' } | Select-Object -First 1
    Assert-True ($null -ne $confItem -and -not $confItem.Transformed -and $confItem.Effective -eq $confItem.Raw) "phase-08 filtered conformance command untouched"

    # 3. phase-09: NO command is transformed (later gates run the full suite).
    $p9File = Get-ChildItem (Join-Path $script:RepoRoot "buildplan\phases") -Filter "phase-09-*.yaml" | Select-Object -First 1
    $gates9 = (python -c $parseScript $p9File.FullName) | ConvertFrom-Json
    $plan9 = Get-EffectiveCommands $gates9 "phase-09"
    $anyTransformed = @($plan9 | Where-Object { $_.Transformed }).Count -gt 0
    Assert-True (-not $anyTransformed) "phase-09 commands never transformed"

    # 4. Working directories: dotnet -> app root (global.json), python -> repo root.
    Assert-True ((Get-CommandWorkdir "dotnet build E:\x\y.csproj") -eq $script:AppRoot) "dotnet commands run from app root"
    Assert-True ((Get-CommandWorkdir "python E:\x\check.py") -eq $script:RepoRoot) "python commands run from repo root"

    # 5. CF_AGENT_ID guard aborts a child invocation (env set in the child only).
    $guardOut = & pwsh -NoProfile -Command "`$env:CF_AGENT_ID='selftest'; & '$PSCommandPath' -ListOnly" 2>&1
    $guardCode = $LASTEXITCODE
    Assert-True ($guardCode -eq 1 -and ($guardOut -join ' ') -match 'CANONICAL surface') "CF_AGENT_ID guard aborts with exit 1"

    if ($script:SelfTestFailures.Count -gt 0) { Write-Host ("SELFTEST: FAIL ({0})" -f ($script:SelfTestFailures -join "; ")); exit 1 }
    Write-Host "SELFTEST: PASS (6/6 assertions)"
    exit 0
}

# --- ListOnly ----------------------------------------------------------------

if ($ListOnly) {
    Write-Host "Phase: $Phase  ($($phaseFile.Name))"
    foreach ($item in $plan) {
        $wd = Get-CommandWorkdir $item.Effective
        $tag = if ($item.Transformed) { " [KnownGap filter appended — phase-08 §12 interpretation]" } else { "" }
        Write-Host ("{0,2}. ({1}) cwd={2}{3}" -f $item.Index, $item.Wave, $wd, $tag)
        Write-Host ("    {0}" -f $item.Effective)
    }
    exit 0
}

# --- ValidateAll: resolve every phase's gate command targets ------------------

if ($ValidateAll) {
    $validateScript = @'
import glob, json, os, re, sys, yaml
root = sys.argv[1]
report = []
for f in sorted(glob.glob(os.path.join(root, "buildplan", "phases", "phase-*.yaml"))):
    d = yaml.safe_load(open(f, encoding="utf-8"))
    g = d.get("orchestrator_gates") or {}
    cmds = list(g.get("after_each_wave") or [])
    for w, c in (g.get("after_waves") or {}).items():
        cmds += list(c or [])
    entries = []
    for cmd in cmds:
        paths = re.findall(r"[A-Za-z]:\\[^\s\"']+", cmd)
        missing = [p for p in paths if not os.path.exists(p)]
        verb = cmd.split()[0]
        known = verb in ("dotnet", "python", "cd", "npm", "npx", "pwsh")
        entries.append({"command": cmd, "paths": paths, "missing": missing, "known_verb": known})
    report.append({"phase_id": d["phase_id"], "file": os.path.basename(f), "commands": entries})
print(json.dumps(report))
'@
    $reportJson = python -c $validateScript $script:RepoRoot
    if ($LASTEXITCODE -ne 0) { Fail-Gate "ValidateAll parse failed." }
    $report = $reportJson | ConvertFrom-Json
    $hardFail = $false
    foreach ($phaseReport in $report) {
        $total = $phaseReport.commands.Count
        $unresolved = @($phaseReport.commands | Where-Object { $_.missing.Count -gt 0 -or -not $_.known_verb })
        if ($phaseReport.phase_id -eq "phase-08") {
            if ($unresolved.Count -gt 0) {
                Write-Host ("FAIL {0}: {1}/{2} commands unresolved:" -f $phaseReport.phase_id, $unresolved.Count, $total)
                foreach ($u in $unresolved) { Write-Host ("  {0}  missing=[{1}] known_verb={2}" -f $u.command, ($u.missing -join ", "), $u.known_verb) }
                $hardFail = $true
            } else {
                Write-Host ("PASS {0}: {1}/{2} gate commands resolve (all targets exist, known verbs)." -f $phaseReport.phase_id, $total, $total)
            }
        } else {
            if ($unresolved.Count -gt 0) {
                Write-Host ("INFO {0}: {1}/{2} commands reference not-yet-built targets (expected before that phase runs):" -f $phaseReport.phase_id, $unresolved.Count, $total)
                foreach ($u in $unresolved) { Write-Host ("  {0}  missing=[{1}]" -f $u.command, ($u.missing -join ", ")) }
            } else {
                Write-Host ("PASS {0}: {1}/{1} gate commands resolve." -f $phaseReport.phase_id, $total)
            }
        }
    }
    if ($hardFail) { exit 1 } else { exit 0 }
}

# --- SDK pin check -----------------------------------------------------------

Push-Location $script:AppRoot
try {
    $sdkVersion = (dotnet --version 2>&1 | Out-String).Trim()
} finally { Pop-Location }
if ($sdkVersion -notmatch '^10\.0\.') {
    Fail-Gate "dotnet --version in app/ returned '$sdkVersion' — expected 10.0.x (app/global.json pin). A .NET 11 preview must not be used; do not edit global.json. Check PATH/SDK installation."
}

# --- execution ---------------------------------------------------------------

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logDir = Join-Path $script:GateRoot "logs\$Phase-$stamp"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$script:SummaryPath = Join-Path $logDir "summary.txt"
Write-GateLog "Phase gates: $Phase ($($phaseFile.Name)) — SDK $sdkVersion, canonical surface (CF_AGENT_ID unset)"
Write-GateLog "Logs: $logDir"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$results = @()
$failed = $false

foreach ($item in $plan) {
    $wd = Get-CommandWorkdir $item.Effective
    $slug = ($item.Effective -replace '[^\w\- ]', '' -replace '\s+', '-').Substring(0, [Math]::Min(48, ($item.Effective -replace '[^\w\- ]', '' -replace '\s+', '-').Length))
    $logFile = Join-Path $logDir ("{0:D2}-{1}.log" -f $item.Index, $slug)
    if ($item.Transformed) {
        Write-GateLog ("#{0} [{1}] KnownGap filter appended (phase-08 §12): {2}" -f $item.Index, $item.Wave, $item.Effective)
    }
    Write-GateLog ("#{0} [{1}] RUN (cwd={2}): {3}" -f $item.Index, $item.Wave, $wd, $item.Effective)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $wd
    try {
        & pwsh -NoProfile -Command $item.Effective 2>&1 | Tee-Object -FilePath $logFile | Out-Host
        $code = $LASTEXITCODE
    } catch {
        Add-Content -LiteralPath $logFile -Value ($_ | Out-String) -Encoding utf8
        $code = 1
    } finally { Pop-Location }
    $sw.Stop()
    $results += [pscustomobject]@{ Index = $item.Index; Wave = $item.Wave; Command = $item.Effective; Exit = $code; Seconds = [int]$sw.Elapsed.TotalSeconds; Log = $logFile }
    if ($code -ne 0) {
        Write-GateLog ("#{0} FAILED exit={1} after {2}s — log: {3}" -f $item.Index, $code, [int]$sw.Elapsed.TotalSeconds, $logFile)
        $failed = $true
        break
    }
    Write-GateLog ("#{0} OK exit=0 ({1}s)" -f $item.Index, [int]$sw.Elapsed.TotalSeconds)

    # check_traceability.py CRLF side effect: restore committed file iff content-identical.
    if ($item.Effective -match 'check_traceability\.py') {
        $traceFile = "architecture/TRACEABILITY.md"
        git -C $script:RepoRoot diff --quiet -- $traceFile 2>$null
        if ($LASTEXITCODE -ne 0) {
            git -C $script:RepoRoot diff --ignore-cr-at-eol --quiet -- $traceFile 2>$null
            if ($LASTEXITCODE -eq 0) {
                git -C $script:RepoRoot checkout -- $traceFile 2>$null
                Write-GateLog "check_traceability.py rewrote TRACEABILITY.md with CRLF (content identical) — restored committed LF version."
            } else {
                Write-GateLog "WARNING: TRACEABILITY.md has a CONTENT diff after check_traceability.py — left untouched; investigate (the gate script never discards content changes)."
            }
        }
    }
}

$stopwatch.Stop()
Write-GateLog ("Summary ({0}s total):" -f [int]$stopwatch.Elapsed.TotalSeconds)
foreach ($r in $results) {
    Write-GateLog ("  #{0} [{1}] exit={2} {3}s  {4}" -f $r.Index, $r.Wave, $r.Exit, $r.Seconds, $r.Command)
}
if ($failed) {
    Write-GateLog "PHASE GATES: FAIL (fail-fast; remaining commands not run)"
    exit 1
}
Write-GateLog "PHASE GATES: PASS — all $($plan.Count) commands exit 0"
exit 0