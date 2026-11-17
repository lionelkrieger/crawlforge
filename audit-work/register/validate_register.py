#!/usr/bin/env python3
"""Validate the CrawlForge gap register.

Checks capabilities.yaml (ID format, uniqueness, all 213 checklist rows
exactly once, extra-capability requirements) and findings.yaml (required
fields, enums, capability references, evidence and source rules) against
finding-schema.yaml. Prints a pass/fail report; exits non-zero on failure.

Requires PyYAML. Run: python validate_register.py
"""

import re
import sys
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent

SECTIONS = ["A", "B", "B2", "C", "D1", "D2", "E", "F", "G", "H", "I", "J", "K", "L"]
# Global checklist row ranges per section (functionality-audit-checklist.md).
ROW_RANGES = {
    "A": (1, 35), "B": (36, 68), "B2": (69, 80), "C": (81, 99),
    "D1": (100, 110), "D2": (111, 118), "E": (119, 138), "F": (139, 152),
    "G": (153, 167), "H": (168, 191), "I": (192, 202), "J": (203, 213),
}
CAP_ID = re.compile(r"^CAP-(A|B|B2|C|D1|D2|E|F|G|H|I|J|K|L)-(\d{3}|X\d{2})$")
FINDING_ID = re.compile(r"^F-\d{4}$")
CAP_FIELDS = ["id", "section", "section_title", "title", "origin",
              "checklist_row", "description", "discovered_in", "notes"]

errors = []
warnings = []


def err(msg):
    errors.append(msg)


def load(name):
    path = HERE / name
    if not path.exists():
        err(f"missing file: {name}")
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            return yaml.safe_load(fh)
    except yaml.YAMLError as exc:
        err(f"{name}: YAML parse error: {exc}")
        return None


def check_capabilities(caps_doc, schema):
    if not isinstance(caps_doc, dict) or not isinstance(caps_doc.get("capabilities"), list):
        err("capabilities.yaml: top-level 'capabilities' list missing")
        return {}
    caps = caps_doc["capabilities"]
    docs = {d["doc"] for d in schema.get("source_documents", [])}
    by_id = {}
    checklist_rows = {}

    for i, cap in enumerate(caps):
        where = f"capabilities[{i}]"
        if not isinstance(cap, dict):
            err(f"{where}: not a mapping")
            continue
        for field in CAP_FIELDS:
            if field not in cap:
                err(f"{where}: missing field '{field}'")
        cid = cap.get("id")
        if not isinstance(cid, str) or not CAP_ID.match(cid):
            err(f"{where}: bad capability ID {cid!r}")
            continue
        where = cid
        if cid in by_id:
            err(f"{where}: duplicate capability ID")
        by_id[cid] = cap

        section = cap.get("section")
        if section not in SECTIONS:
            err(f"{where}: unknown section {section!r}")
        elif CAP_ID.match(cid).group(1) != section:
            err(f"{where}: ID section does not match section field")
        if not isinstance(cap.get("title"), str) or not cap.get("title", "").strip():
            err(f"{where}: empty title")

        origin = cap.get("origin")
        suffix = CAP_ID.match(cid).group(2)
        if origin == "checklist":
            row = cap.get("checklist_row")
            if not isinstance(row, int) or not (1 <= row <= 213):
                err(f"{where}: checklist_row must be an int 1..213, got {row!r}")
            else:
                if suffix != f"{row:03d}":
                    err(f"{where}: ID suffix {suffix} does not match checklist_row {row}")
                lo, hi = ROW_RANGES[section]
                if not (lo <= row <= hi):
                    err(f"{where}: row {row} outside section {section} range {lo}-{hi}")
                if row in checklist_rows:
                    err(f"{where}: checklist row {row} appears more than once")
                checklist_rows[row] = cid
            if cap.get("description") is not None:
                err(f"{where}: checklist entries must have description null")
        elif origin == "extra":
            if not suffix.startswith("X"):
                err(f"{where}: extra capability must use an Xnn suffix")
            if cap.get("checklist_row") is not None:
                err(f"{where}: extras must have checklist_row null")
            desc = cap.get("description")
            if not isinstance(desc, str) or not desc.strip():
                err(f"{where}: extras require a one-sentence description")
            srcs = cap.get("discovered_in")
            if not isinstance(srcs, list) or not srcs:
                err(f"{where}: extras require a non-empty discovered_in list")
            else:
                for s in srcs:
                    if not isinstance(s, dict) or s.get("doc") not in docs \
                            or not isinstance(s.get("locator"), str) or not s["locator"].strip():
                        err(f"{where}: discovered_in entry needs a catalogue doc and a locator: {s!r}")
        else:
            err(f"{where}: origin must be 'checklist' or 'extra', got {origin!r}")

    missing = [r for r in range(1, 214) if r not in checklist_rows]
    if missing:
        err(f"checklist rows missing from spine: {missing}")

    for i, rej in enumerate(caps_doc.get("rejected_duplicates", []) or []):
        where = f"rejected_duplicates[{i}]"
        if not isinstance(rej, dict) or not rej.get("candidate"):
            err(f"{where}: needs a candidate name")
            continue
        row = rej.get("maps_to_row")
        merged = rej.get("merged_into")
        if row is not None and (not isinstance(row, int) or row not in checklist_rows):
            err(f"{where}: maps_to_row {row!r} is not a checklist row in the spine")
        if merged is not None and merged not in by_id:
            err(f"{where}: merged_into {merged!r} is not a capability ID")
        if row is None and merged is None:
            err(f"{where}: needs maps_to_row or merged_into")

    return by_id


def check_findings(findings_doc, schema, by_id):
    if not isinstance(findings_doc, dict) or not isinstance(findings_doc.get("findings"), list):
        err("findings.yaml: top-level 'findings' list missing")
        return
    findings = findings_doc["findings"]
    fields = schema.get("finding", {}).get("fields", {})
    enums = schema.get("enums", {})
    docs = {d["doc"] for d in schema.get("source_documents", [])}
    seen = set()

    for i, f in enumerate(findings):
        where = f"findings[{i}]"
        if not isinstance(f, dict):
            err(f"{where}: not a mapping")
            continue
        fid = f.get("id")
        if isinstance(fid, str) and FINDING_ID.match(fid):
            where = fid
            if fid in seen:
                err(f"{where}: duplicate finding ID")
            seen.add(fid)
        else:
            err(f"{where}: bad finding ID {fid!r} (expected F-nnnn)")

        for name, spec in fields.items():
            if spec.get("required") and name not in f:
                err(f"{where}: missing required field '{name}'")
        for name in f:
            if name not in fields:
                err(f"{where}: unknown field '{name}'")

        cap = f.get("capability")
        if cap not in by_id:
            err(f"{where}: capability {cap!r} not in capabilities.yaml")

        stmt = f.get("statement")
        if not isinstance(stmt, str) or not stmt.strip():
            err(f"{where}: statement must be a non-empty string")

        for name in ("gap_type", "severity", "status"):
            val = f.get(name)
            allowed = enums.get(name, {})
            if val not in allowed:
                err(f"{where}: {name} {val!r} not in enum {sorted(allowed)}")

        layers = f.get("layers")
        allowed_layers = set(enums.get("layers", {}))
        if not isinstance(layers, list) or not layers:
            err(f"{where}: layers must be a non-empty list")
        else:
            for layer in layers:
                if layer not in allowed_layers:
                    err(f"{where}: unknown layer {layer!r}")
            if len(set(layers)) != len(layers):
                err(f"{where}: duplicate layers")

        gap_type = f.get("gap_type")
        evidence = f.get("evidence")
        if not isinstance(evidence, list):
            err(f"{where}: evidence must be a list")
        else:
            if not evidence and gap_type not in ("spec_gap", "design_decision"):
                err(f"{where}: evidence empty but gap_type is {gap_type!r} "
                    "(empty allowed only for spec_gap / design_decision)")
            for ev in evidence:
                if not isinstance(ev, dict) or not all(
                        isinstance(ev.get(k), str) and ev.get(k, "").strip()
                        for k in ("path", "symbol", "note")):
                    err(f"{where}: evidence item needs path, symbol and note: {ev!r}")

        sources = f.get("sources")
        if not isinstance(sources, list) or not sources:
            err(f"{where}: sources must have at least one entry")
        else:
            for s in sources:
                if not isinstance(s, dict):
                    err(f"{where}: source entry not a mapping: {s!r}")
                    continue
                if s.get("doc") not in docs:
                    err(f"{where}: source doc {s.get('doc')!r} not in the catalogue")
                if not isinstance(s.get("locator"), str) or not s["locator"].strip():
                    err(f"{where}: source needs a locator")
                if not isinstance(s.get("claim"), str) or not s["claim"].strip():
                    err(f"{where}: source needs a one-line claim")
                sid = s.get("source_id")
                if sid is not None and not isinstance(sid, str):
                    err(f"{where}: source_id must be a string or null")

        status = f.get("status")
        conflict = f.get("conflict")
        if conflict is not None and not isinstance(conflict, str):
            err(f"{where}: conflict must be a string or null")
        if status == "conflict" and not conflict:
            err(f"{where}: status is conflict but conflict text is empty")

        if not isinstance(f.get("decision_needed"), bool):
            err(f"{where}: decision_needed must be a bool")
        for name in ("flow", "notes"):
            val = f.get(name)
            if val is not None and not isinstance(val, str):
                err(f"{where}: {name} must be a string or null")


def main():
    schema = load("finding-schema.yaml")
    caps_doc = load("capabilities.yaml")
    findings_doc = load("findings.yaml")
    if schema is None or caps_doc is None or findings_doc is None:
        report()
        return 1

    by_id = check_capabilities(caps_doc, schema)
    check_findings(findings_doc, schema, by_id)

    checklist = [c for c in by_id.values() if c.get("origin") == "checklist"]
    extras = [c for c in by_id.values() if c.get("origin") == "extra"]
    findings = findings_doc.get("findings", []) if isinstance(findings_doc, dict) else []
    print(f"Capabilities: {len(by_id)} total | {len(checklist)} checklist rows | {len(extras)} extras")
    print(f"Findings: {len(findings)}")
    report()
    return 1 if errors else 0


def report():
    if errors:
        print(f"\nFAIL: {len(errors)} error(s)")
        for e in errors:
            print(f"  ERROR  {e}")
    else:
        print("\nPASS: capability spine and findings are valid.")
    if warnings:
        for w in warnings:
            print(f"  WARN   {w}")


if __name__ == "__main__":
    sys.exit(main())