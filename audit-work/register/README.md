# CrawlForge gap register — spine and format (step 1)

This directory holds the consolidated gap register for CrawlForge
(`E:\Crawlforge\app`): the capability spine every finding attaches to, the
finding format, an empty register, a validator and a Markdown renderer.

**Step 1 only.** No findings have been extracted from the audit documents
yet. `findings.yaml` is intentionally empty. Extraction and consolidation
follow the process below.

## Files

| File | Role |
|---|---|
| `capabilities.yaml` | The spine: 213 checklist capabilities + 60 extra capabilities, plus the rejected-duplicate list at the end. |
| `finding-schema.yaml` | The finding contract: exact fields, enums with definitions, and the source document catalogue (every audit document, its short name, ID scheme and locator convention). |
| `findings.yaml` | The register itself. Currently `findings: []`. |
| `validate_register.py` | Checks the spine and every finding against the schema; prints PASS/FAIL, exits non-zero on failure. |
| `render_register.py` | Generates `register.md` from the YAML files. Deterministic output. |
| `register.md` | Generated report. **Do not edit by hand** — edit the YAML and re-render. |

Run from anywhere (paths resolve relative to the scripts):

```
python validate_register.py
python render_register.py
```

Requires Python 3 and PyYAML (already installed: PyYAML 6.0.3).

## ID rules

- **Checklist capabilities:** `CAP-<section>-<row>`, where `<row>` is the
  global row number from `functionality-audit-checklist.md`, zero-padded to
  3 digits. Row 1 → `CAP-A-001`, row 69 → `CAP-B2-069`, row 213 →
  `CAP-J-213`. **These IDs never change.**
- **Extra capabilities** (in the audits but not on the checklist):
  `CAP-<section>-X<nn>`, numbered per section from `X01`, assigned to the
  best-matching section. Sub-capabilities of a checklist row are *not*
  extras; they are recorded in that row's `notes`.
- **Findings:** `F-<nnnn>`, sequential, assigned at merge time. Never
  reused, even for rejected findings.
- Sections and row ranges: A 1–35, B 36–68, B2 69–80, C 81–99,
  D1 100–110, D2 111–118, E 119–138, F 139–152, G 153–167, H 168–191,
  I 192–202, J 203–213. Sections K (Projects and configuration) and L
  (Platform) hold extras only. Owner decisions are in `../decisions-log.md`
  and are noted on the affected capabilities.

## Enums (full definitions in `finding-schema.yaml`)

- `gap_type`: `unwired` (code exists, never registered/invoked in the
  running product), `missing_ui` (works via engine/API, no reachable UI),
  `missing_engine` (no implementation), `spec_gap` (not specified),
  `design_decision` (product choice needed before building), `defect`
  (implemented but behaves incorrectly).
- `layers`: `engine`, `wiring`, `api`, `ui` — which layers a finding
  affects (non-empty subset).
- `severity`: `P0` product-breaking (core workflow cannot complete or
  results are wrong), `P1` major capability missing or misleading,
  `P2` degraded or incomplete, `P3` minor/polish. Consistent with
  UNDERBUILD-AUDIT.md §4. When extracting from documents that use
  critical/high/medium/low (ux-api-gap-audit.md, 04-gap-analysis.md), map
  critical → P0, high → P1, medium → P2, low → P3.
- `status`: `extracted` (copied from one source, unmerged), `merged`
  (deduplicated across sources), `conflict` (sources disagree, unresolved;
  `conflict` field must describe the disagreement), `verified` (confirmed
  against code by an independent check), `rejected` (shown wrong or not a
  gap).

Evidence is required for every finding except `spec_gap` and
`design_decision`. Every finding cites at least one source document from
the catalogue in `finding-schema.yaml`, with a locator and a one-line
claim; `source_id` carries the document's own ID (G07, GAP-08, B1-08, …)
when it has one.

## Consolidation process (the steps that follow)

1. **Extract per source document.** One pass per audit document, in
   catalogue order; each finding starts as `status: extracted` with its
   own source entry. Do not blend documents during extraction.
2. **Merge by capability ID.** Findings describing the same gap on the
   same capability become one record (`status: merged`) carrying every
   source entry. Assign `F-nnnn` IDs at merge time. Disagreements become
   `status: conflict` with the `conflict` field filled.
3. **Verify conflicts and high-severity items against code.** P0/P1
   findings and every conflict get an independent code check; confirmed
   ones move to `status: verified`, disproven ones to `rejected`.
4. **Separate decisions from build work.** `decision_needed: true` items
   (mostly `spec_gap` / `design_decision`) go to the owner; the rest is
   build work.
5. **Map build work to end-to-end flows.** Fill the `flow` field so fixes
   can be sequenced by user-visible flow rather than by subsystem.

## Precedence rule

**Code evidence beats any audit's claim. A newer audit beats an older one
only if it cites code.** When two documents disagree and neither cites
code, the item stays `conflict` until step 3 resolves it against the
source tree.

## Scope notes

- The app tree (`E:\Crawlforge\app`) is read-only context for this work;
  nothing in it is modified by the register tooling.
- `register.md` regenerates byte-identically from unchanged inputs (no
  timestamps), so diffs reflect only register changes.