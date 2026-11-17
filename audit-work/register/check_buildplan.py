#!/usr/bin/env python3
"""Build plan v2.0 coverage check.

Reads buildplan/index.yaml, buildplan/phases/phase-*.yaml,
buildplan/capability-baseline.yaml, the capability register, the UI surface
map and the fixture expectations, and fails when:

  1. an in-scope capability whose baseline state is not `works` is covered
     by no workpiece in a scheduled phase;
  2. a workpiece cites no capabilities, no architecture sections, or no
     fixture traps (UI workpieces also need ui_surfaces);
  3. a cited capability, surface, trap, criterion or architecture section
     does not exist (or a cited baseline state disagrees with the baseline);
  4. a surface bound in the UI map is made reachable by no workpiece,
     unless every capability bound to it is already `works`;
  5. file ownership overlaps within a wave (path-prefix containment,
     case-insensitive).

Also checks ledger consistency: index phase lists vs phase files, criteria
ledger references, dependency targets, wave membership, and that the
baseline covers exactly the in-scope capability set.

Unscheduled phases (phase-16, distribution) have their citations validated
but do not count toward coverage.

Exit code 0 = pass.
"""
import json
import re
import sys
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
PLAN = ROOT / "buildplan"
ARCH = ROOT / "architecture"
UIMAP = ARCH / "06-frontend-shell-and-ui" / "04-ui-surface-map.md"
EXPECTED = ROOT / "app" / "tests" / "fixture-site" / "expected-results.json"

errors = []


def err(msg):
    errors.append(msg)


def load_yaml(path):
    return yaml.safe_load(path.read_text(encoding="utf-8"))


# --- inputs ---------------------------------------------------------------
index = load_yaml(PLAN / "index.yaml")
baseline_doc = load_yaml(PLAN / "capability-baseline.yaml")
baseline = {c["id"]: c for c in baseline_doc["capabilities"]}
register = load_yaml(HERE / "capabilities.yaml")
caps = {c["id"] for c in register["capabilities"]}
untagged = set(load_yaml(HERE / "untagged.yaml")["untagged"].keys())
in_scope = caps - untagged

expected = json.loads(EXPECTED.read_text(encoding="utf-8"))
traps = expected["traps"]
trap_ids = set(traps.keys()) if isinstance(traps, dict) else {t["id"] for t in traps}
trap_ids |= {g["id"] for g in expected.get("global_assertions", [])}

criteria_ids = {e["criterion_id"] for e in index["acceptance_criteria_ledger"]}

# --- UI map: surface catalogue + binding table (same parsing as
# --- check_traceability.py) ----------------------------------------------
surfaces, bound = set(), {}
section = None
for line in UIMAP.read_text(encoding="utf-8").split("\n"):
    if line.startswith("### "):
        section = line
    m = re.match(r"^\| (UI-\d\d) \|", line)
    if m and section and "Surface catalogue" in section:
        surfaces.add(m.group(1))
    m = re.match(r"^\| (CAP-[A-Z0-9]+-[X0-9]+) \|", line)
    if m and section and "Binding table" in section:
        cols = [c.strip() for c in line.strip("|").split(" | ")]
        bound[m.group(1)] = " ".join(cols[2:5])

cap_surfaces = {}
for cid, text in bound.items():
    refs = set(re.findall(r"UI-\d\d", text))
    for lo, hi in re.findall(r"UI-(\d\d)\.\.UI-(\d\d)", text):
        refs |= {f"UI-{n:02d}" for n in range(int(lo), int(hi) + 1)}
    cap_surfaces[cid] = refs

surface_caps = {}
for cid, refs in cap_surfaces.items():
    for r in refs:
        surface_caps.setdefault(r, set()).add(cid)

# --- architecture section headings ----------------------------------------
_heading_cache = {}


def heading_numbers(path):
    if path not in _heading_cache:
        nums = set()
        if Path(path).exists():
            for line in Path(path).read_text(encoding="utf-8").split("\n"):
                if line.startswith("#"):
                    m = re.match(r"^#+\s*(\d+(?:\.\d+)*)[.\s]", line)
                    if m:
                        nums.add(m.group(1))
        _heading_cache[path] = nums
    return _heading_cache[path]


# --- baseline integrity ----------------------------------------------------
if set(baseline) != in_scope:
    for cid in sorted(in_scope - set(baseline)):
        err(f"baseline: missing in-scope capability {cid}")
    for cid in sorted(set(baseline) - in_scope):
        err(f"baseline: {cid} is not in scope (deferred/out-of-scope/via-MCP)")
VALID_STATES = {"works", "partial", "unwired", "missing_ui", "missing_engine", "defect"}
VALID_EVIDENCE = {"runtime", "code", "claim"}
for cid, c in baseline.items():
    if c["state"] not in VALID_STATES:
        err(f"baseline {cid}: invalid state {c['state']}")
    if c["evidence_level"] not in VALID_EVIDENCE:
        err(f"baseline {cid}: invalid evidence_level {c['evidence_level']}")
    if c["state"] == "works" and c["evidence_level"] != "runtime":
        err(f"baseline {cid}: works requires runtime evidence")
    if "\n" in c.get("evidence", ""):
        err(f"baseline {cid}: evidence must be one line")

# --- phase files ------------------------------------------------------------
phases = {}
workpieces = {}
for f in sorted((PLAN / "phases").glob("phase-*.yaml")):
    d = load_yaml(f)
    pid = d["phase_id"]
    phases[pid] = d
    wavemap = {}
    for wv in d.get("parallel_waves", []):
        for wid in wv["workpiece_ids"]:
            wavemap[wid] = wv["wave_id"]
    by_wave = {}
    for w in d["workpieces"]:
        wid = w["workpiece_id"]
        workpieces[wid] = (pid, w)
        if wid in wavemap and wavemap[wid] != w.get("parallel_group"):
            err(f"{wid}: parallel_group does not match its wave entry")
        if wid not in wavemap:
            err(f"{wid}: listed in no wave")
        by_wave.setdefault(w.get("parallel_group"), []).append(w)

    # rule 5: ownership overlap within a wave
    for wave, ws in by_wave.items():
        paths = [
            (w["workpiece_id"], p.lower().rstrip("\\").rstrip("/"))
            for w in ws
            for p in w.get("owned_paths", [])
        ]
        for i in range(len(paths)):
            for j in range(i + 1, len(paths)):
                (wa, a), (wb, b) = paths[i], paths[j]
                if a == b or a.startswith(b + "\\") or b.startswith(a + "\\"):
                    err(f"ownership overlap in {wave}: {wa} '{a}' vs {wb} '{b}'")

    # rules 2+3: citations
    for w in d["workpieces"]:
        wid = w["workpiece_id"]
        cited_caps = w.get("capabilities") or []
        cited_secs = w.get("architecture_sections") or []
        cited_traps = w.get("fixture_traps") or []
        if not cited_caps:
            err(f"{wid}: cites no capabilities")
        if not cited_secs:
            err(f"{wid}: cites no architecture sections")
        if not cited_traps:
            err(f"{wid}: cites no fixture traps")
        is_ui = any(
            ("src\\frontend\\" in p or "src/frontend/" in p
             or "src\\electron\\" in p or "src/electron/" in p)
            for p in w.get("owned_paths", [])
        )
        if is_ui and not w.get("ui_surfaces"):
            err(f"{wid}: UI workpiece (owns frontend/electron paths) cites no ui_surfaces")
        for c in cited_caps:
            cid = c["id"]
            if cid not in caps:
                err(f"{wid}: unknown capability {cid}")
            elif cid not in in_scope:
                err(f"{wid}: capability {cid} is not in scope")
            elif cid in baseline and c.get("baseline_state") != baseline[cid]["state"]:
                err(f"{wid}: baseline_state '{c.get('baseline_state')}' for {cid} "
                    f"disagrees with baseline '{baseline[cid]['state']}'")
        for s in w.get("ui_surfaces") or []:
            if s not in surfaces:
                err(f"{wid}: unknown surface {s}")
        for t in cited_traps:
            if t not in trap_ids:
                err(f"{wid}: unknown fixture trap {t}")
        for cr in w.get("acceptance_criteria_refs") or []:
            if cr not in criteria_ids:
                err(f"{wid}: unknown acceptance criterion #{cr}")
        for a in cited_secs:
            fpath = a.get("file", "")
            if not Path(fpath).exists():
                err(f"{wid}: architecture file does not exist: {fpath}")
                continue
            nums = heading_numbers(fpath)
            for s in a.get("sections", []):
                if str(s) not in nums:
                    err(f"{wid}: section {s} is not a numbered heading in {Path(fpath).name}")

# dependency targets (after all files loaded)
for wid, (pid, w) in workpieces.items():
    for dep in w.get("depends_on_workpieces") or []:
        if dep not in workpieces:
            err(f"{wid}: depends_on_workpieces target {dep} does not exist")

# --- index consistency ------------------------------------------------------
declared_phases = {p["phase_id"] for p in index["phases"]}
if declared_phases != set(phases):
    err(f"index phases {sorted(declared_phases)} != phase files {sorted(phases)}")
for p in index["phases"]:
    pid = p["phase_id"]
    if pid in phases:
        actual = [w["workpiece_id"] for w in phases[pid]["workpieces"]]
        if p.get("workpiece_ids") != actual:
            err(f"index {pid}: workpiece_ids disagree with phase file")
for e in index["acceptance_criteria_ledger"]:
    if e["workpiece_id"] not in workpieces:
        err(f"criterion #{e['criterion_id']}: ledger workpiece {e['workpiece_id']} does not exist")
    if e["phase_id"] not in phases:
        err(f"criterion #{e['criterion_id']}: ledger phase {e['phase_id']} does not exist")
meta = index.get("metadata", {})
if meta.get("total_workpieces") != len(workpieces):
    err(f"index metadata total_workpieces {meta.get('total_workpieces')} != {len(workpieces)}")
if meta.get("total_phases") != len(phases):
    err(f"index metadata total_phases {meta.get('total_phases')} != {len(phases)}")

# --- rules 1+4: coverage (scheduled phases only) ----------------------------
scheduled = {pid: d for pid, d in phases.items() if d.get("status") != "unscheduled"}
covered_caps = set()
covered_surfaces = set()
for pid, d in scheduled.items():
    for w in d["workpieces"]:
        covered_caps |= {c["id"] for c in w.get("capabilities") or []}
        covered_surfaces |= set(w.get("ui_surfaces") or [])

for cid in sorted(in_scope):
    if baseline[cid]["state"] != "works" and cid not in covered_caps:
        err(f"capability {cid} (state={baseline[cid]['state']}) is covered by no workpiece")

for s in sorted(surfaces):
    bcaps = surface_caps.get(s, set())
    if bcaps and all(baseline.get(c, {}).get("state") == "works" for c in bcaps):
        continue  # already works end to end; no workpiece required
    if s not in covered_surfaces:
        err(f"surface {s} is bound in the UI map but made reachable by no workpiece")

# --- verdict ----------------------------------------------------------------
if errors:
    print("FAIL")
    for e in errors:
        print(" ", e)
    sys.exit(1)

states = {}
for c in baseline.values():
    states[c["state"]] = states.get(c["state"], 0) + 1
need = sum(1 for c in baseline.values() if c["state"] != "works")
print(
    f"PASS: {len(workpieces)} workpieces in {len(phases)} phases "
    f"({len(scheduled)} scheduled); {len(in_scope)} in-scope capabilities, "
    f"{need} non-works all covered; {len(covered_surfaces)} surfaces cited; "
    f"{len(trap_ids)} traps known; {len(criteria_ids)} criteria; "
    f"no ownership overlaps. Baseline states: "
    + ", ".join(f"{k}={v}" for k, v in sorted(states.items()))
)