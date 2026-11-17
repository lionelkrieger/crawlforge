#!/usr/bin/env python3
"""Render register.md from the CrawlForge gap register YAML files.

Reads capabilities.yaml, findings.yaml and finding-schema.yaml (for enum
ordering and the source catalogue) and writes register.md:

  - summary counts by severity, gap type, status and section, plus the
    number of decision_needed items and conflicts;
  - one section per capability section, each capability listed with its
    findings (ID, severity, gap type, layers, statement, evidence paths,
    source docs);
  - separate "Decisions needed" and "Conflicts" lists;
  - capabilities with no findings listed compactly at the end of each
    section;
  - an appendix of rejected duplicate candidates.

Output is deterministic (no timestamp), so re-running on unchanged inputs
produces a byte-identical register.md. Do not edit register.md by hand.

Requires PyYAML. Run: python render_register.py
"""

import sys
from collections import Counter, defaultdict
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent

SECTIONS = ["A", "B", "B2", "C", "D1", "D2", "E", "F", "G", "H", "I", "J", "K", "L"]
SECTION_TITLES = {
    "A": "Crawl engine",
    "B": "Technical SEO analysis",
    "B2": "Structured data engine",
    "C": "Performance analysis",
    "D1": "Accessibility",
    "D2": "Security",
    "E": "Information architecture and site understanding",
    "F": "Content intelligence",
    "G": "Content extraction",
    "H": "Reporting, data and integrations",
    "I": "Findings and prioritisation",
    "J": "AI-assisted analysis",
    "K": "Projects and configuration",
    "L": "Platform",
}
SEVERITY_ORDER = ["P0", "P1", "P2", "P3"]


def load(name):
    path = HERE / name
    if not path.exists():
        print(f"ERROR: missing file: {path}", file=sys.stderr)
        sys.exit(1)
    with open(path, encoding="utf-8") as fh:
        return yaml.safe_load(fh)


def cell(text):
    """Make a string safe inside a Markdown table cell."""
    return str(text).replace("|", "\\|").replace("\n", " ")


def cap_sort_key(cap):
    if cap.get("origin") == "checklist":
        return (0, cap.get("checklist_row") or 0, "")
    suffix = cap["id"].rsplit("-", 1)[-1]  # e.g. X07
    num = int(suffix[1:]) if suffix.startswith("X") and suffix[1:].isdigit() else 999
    return (1, num, cap["id"])


def enum_order(schema, name, fallback):
    keys = list(schema.get("enums", {}).get(name, {}).keys())
    return keys or fallback


def render_finding(f):
    """One finding as a Markdown bullet block."""
    head = (f"**{f['id']}** · {f['severity']} · {f['gap_type']} · "
            f"layers: {', '.join(f['layers'])} · status: {f['status']}")
    if f.get("decision_needed"):
        head += " · DECISION NEEDED"
    lines = [f"- {head}", f"  - {f['statement']}"]

    evidence = f.get("evidence") or []
    if evidence:
        parts = [f"`{ev['path']}` ({ev['symbol']}) — {ev['note']}" for ev in evidence]
        lines.append("  - Evidence: " + "; ".join(parts))
    else:
        lines.append("  - Evidence: none required "
                     f"({f['gap_type']})")

    sources = f.get("sources") or []
    parts = []
    for s in sources:
        ref = f"{s['doc']} {s['locator']}"
        if s.get("source_id"):
            ref += f" [{s['source_id']}]"
        parts.append(ref)
    lines.append("  - Sources: " + "; ".join(parts))

    if f.get("conflict"):
        lines.append(f"  - Conflict: {f['conflict']}")
    if f.get("flow"):
        lines.append(f"  - Flow: {f['flow']}")
    if f.get("notes"):
        lines.append(f"  - Notes: {f['notes']}")
    return lines


def render_capability(cap, findings):
    lines = [f"### {cap['id']} — {cap['title']}", ""]
    if cap.get("origin") == "checklist":
        lines.append(f"*Checklist row {cap['checklist_row']}.*")
    else:
        lines.append(f"*Extra capability.* {cap.get('description') or ''}")
        discovered = cap.get("discovered_in") or []
        if discovered:
            refs = "; ".join(f"{s['doc']} {s['locator']}" for s in discovered)
            lines.append(f"Discovered in: {refs}")
    if cap.get("notes"):
        lines.append(f"Notes: {cap['notes']}")
    lines.append("")
    for f in sorted(findings, key=lambda x: x["id"]):
        lines.extend(render_finding(f))
        lines.append("")
    return lines


def main():
    caps_doc = load("capabilities.yaml")
    findings_doc = load("findings.yaml")
    schema = load("finding-schema.yaml")

    caps = caps_doc.get("capabilities") or []
    findings = findings_doc.get("findings") or []
    rejected = caps_doc.get("rejected_duplicates") or []

    by_id = {c["id"]: c for c in caps}
    by_cap = defaultdict(list)
    unattached = []
    for f in findings:
        if f.get("capability") in by_id:
            by_cap[f["capability"]].append(f)
        else:
            unattached.append(f)

    checklist = [c for c in caps if c.get("origin") == "checklist"]
    extras = [c for c in caps if c.get("origin") == "extra"]
    decisions = [f for f in findings if f.get("decision_needed")]
    conflicts = [f for f in findings if f.get("status") == "conflict"]

    gap_order = enum_order(schema, "gap_type",
                           ["unwired", "missing_ui", "missing_engine",
                            "spec_gap", "design_decision", "defect"])
    status_order = enum_order(schema, "status",
                              ["extracted", "merged", "conflict", "verified", "rejected"])

    sev_counts = Counter(f.get("severity") for f in findings)
    gap_counts = Counter(f.get("gap_type") for f in findings)
    status_counts = Counter(f.get("status") for f in findings)

    out = []
    out.append("# CrawlForge gap register")
    out.append("")
    out.append("Generated by `render_register.py` from `capabilities.yaml` and "
               "`findings.yaml` (schema: `finding-schema.yaml`). Do not edit this "
               "file by hand; edit the YAML and re-render.")
    out.append("")

    # ---- Summary -----------------------------------------------------------
    out.append("## Summary")
    out.append("")
    out.append(f"- Capabilities: **{len(caps)}** "
               f"({len(checklist)} checklist rows, {len(extras)} extras)")
    out.append(f"- Findings: **{len(findings)}**")
    out.append(f"- Decisions needed: **{len(decisions)}**")
    out.append(f"- Conflicts: **{len(conflicts)}**")
    out.append("")

    out.append("| Severity | Count |")
    out.append("|---|---|")
    for sev in SEVERITY_ORDER:
        out.append(f"| {sev} | {sev_counts.get(sev, 0)} |")
    out.append("")

    out.append("| Gap type | Count |")
    out.append("|---|---|")
    for gap in gap_order:
        out.append(f"| {gap} | {gap_counts.get(gap, 0)} |")
    out.append("")

    out.append("| Status | Count |")
    out.append("|---|---|")
    for st in status_order:
        out.append(f"| {st} | {status_counts.get(st, 0)} |")
    out.append("")

    out.append("| Section | Title | Capabilities (checklist + extras) | Findings |")
    out.append("|---|---|---|---|")
    for sec in SECTIONS:
        sec_caps = [c for c in caps if c.get("section") == sec]
        n_check = sum(1 for c in sec_caps if c.get("origin") == "checklist")
        n_extra = len(sec_caps) - n_check
        n_find = sum(len(by_cap[c["id"]]) for c in sec_caps)
        title = sec_caps[0].get("section_title") if sec_caps else SECTION_TITLES[sec]
        out.append(f"| {sec} | {cell(title)} | {len(sec_caps)} ({n_check} + {n_extra}) | {n_find} |")
    out.append("")

    # ---- Decisions needed --------------------------------------------------
    out.append("## Decisions needed")
    out.append("")
    if decisions:
        for f in sorted(decisions, key=lambda x: x["id"]):
            out.append(f"- **{f['id']}** ({f['capability']}, {f['severity']}): "
                       f"{f['statement']}")
    else:
        out.append("None.")
    out.append("")

    # ---- Conflicts ---------------------------------------------------------
    out.append("## Conflicts")
    out.append("")
    if conflicts:
        for f in sorted(conflicts, key=lambda x: x["id"]):
            out.append(f"- **{f['id']}** ({f['capability']}): {f.get('conflict')}")
    else:
        out.append("None.")
    out.append("")

    # ---- Per-section capability detail -------------------------------------
    for sec in SECTIONS:
        sec_caps = sorted((c for c in caps if c.get("section") == sec),
                          key=cap_sort_key)
        title = sec_caps[0].get("section_title") if sec_caps else SECTION_TITLES[sec]
        out.append(f"## Section {sec} — {title}")
        out.append("")
        with_findings = [c for c in sec_caps if by_cap.get(c["id"])]
        without = [c for c in sec_caps if not by_cap.get(c["id"])]
        if not with_findings:
            out.append("No findings recorded in this section yet.")
            out.append("")
        for cap in with_findings:
            out.extend(render_capability(cap, by_cap[cap["id"]]))
        if without:
            out.append("**Capabilities with no findings:**")
            out.append("")
            parts = []
            for c in without:
                marker = (str(c["checklist_row"]) if c.get("origin") == "checklist"
                          else "extra")
                parts.append(f"`{c['id']}` ({marker}) {c['title']}")
            out.append(" · ".join(parts))
            out.append("")

    # ---- Unattached findings (defensive; validator should catch these) -----
    if unattached:
        out.append("## Unattached findings (capability not in spine)")
        out.append("")
        for f in sorted(unattached, key=lambda x: x.get("id", "")):
            out.extend(render_finding(f))
            out.append("")

    # ---- Appendix: rejected duplicates -------------------------------------
    out.append("## Appendix — rejected duplicate candidates")
    out.append("")
    if rejected:
        out.append("Candidates considered as extra capabilities but rejected as "
                   "duplicates of checklist rows or merged into other extras "
                   "(from `capabilities.yaml: rejected_duplicates`).")
        out.append("")
        out.append("| Candidate | Source | Maps to | Reason |")
        out.append("|---|---|---|---|")
        for r in rejected:
            if r.get("maps_to_row") is not None:
                target = f"checklist row {r['maps_to_row']}"
            else:
                target = r.get("merged_into") or "?"
            out.append(f"| {cell(r.get('candidate', ''))} | {cell(r.get('source', ''))} "
                       f"| {cell(target)} | {cell(r.get('reason', ''))} |")
        out.append("")
    else:
        out.append("None.")
        out.append("")

    target = HERE / "register.md"
    target.write_text("\n".join(out), encoding="utf-8")
    print(f"Wrote {target} ({len(caps)} capabilities, {len(findings)} findings, "
          f"{len(decisions)} decisions needed, {len(conflicts)} conflicts)")
    return 0


if __name__ == "__main__":
    sys.exit(main())