# Task: Build the capability spine and finding-register format for the CrawlForge gap register

You are setting up the structure that all CrawlForge audit findings will be consolidated into. This task is **step 1 only**: define the spine (capability IDs), the finding format, an empty register, a validator and a Markdown renderer. **Do not extract or write any findings from the audit documents.** That is a later step done per source document.

Do not modify any existing file. Do not touch `E:\Crawlforge\app`. Create new files only inside `E:\Crawlforge\audit-work\register\`. Do not commit anything.

## Context

CrawlForge is a desktop website crawler and SEO auditor (.NET backend, Electron shell, React frontend) at `E:\Crawlforge\app`. A first build produced most libraries but left much of it unwired or without UI. Several audits were written today, in parallel, by different sessions, each with its own structure and numbering. They now need consolidating into one register without losing or blending information.

All audit material is in `E:\Crawlforge\audit-work\`:

- `functionality-audit-checklist.md`: 213 numbered feature rows in sections A, B, B2, C, D1, D2, E, F, G, H, I, J. **This is the backbone.**
- `05-functionality-audit-results.md`: per-row results for the 213 rows, plus section 3 "Capabilities not on the checklist" grouped by suggested section.
- `project-configuration-review.md`: configuration, rendering and site-type review. Sections 3, 4 and 5 describe capabilities and decisions, several not on the checklist (for example cookie jar, hash-route handling, postback detection, crawler-trap detection, consent-state rendering, user-agent comparison, capture modes, direct CDP layer, pinned bundled browser, configuration editor, per-project thresholds, client context).
- `UNDERBUILD-AUDIT.md`, `ux-api-gap-audit.md` (uses GAP-nn IDs), `04-gap-analysis.md` (uses IDs such as CRAWL-001), `04a-ux-api-join.md`, `04b-docs-channel-join.md`, `01-api-exposure-inventory.md`, `02-ux-surface-inventory.md`, `03-documented-feature-inventory.md`. Read these only to (a) learn each document's own ID scheme and section structure for source references, and (b) find capabilities that are not on the checklist.

## Deliverables (all in `E:\Crawlforge\audit-work\register\`)

### 1. `capabilities.yaml`: the spine

Every capability that findings can attach to.

- **Checklist capabilities:** one entry per checklist row, all 213. ID format `CAP-<section>-<row>` where `<row>` is the checklist's global row number, zero-padded to 3 digits. Examples: row 1 → `CAP-A-001`, row 69 → `CAP-B2-069`, row 213 → `CAP-J-213`. These IDs must never change.
- **Extra capabilities** (found in the audits but not on the checklist): ID format `CAP-<section>-X<nn>`, numbered per section from `X01`. Assign each to the best-matching existing section. Sources: `05-functionality-audit-results.md` section 3 first, then `project-configuration-review.md`, then anything else the other documents clearly describe as a capability. One capability per entry. Do not duplicate a capability that already maps to a checklist row; if an extra is a sub-capability of a row, attach it to that row instead and record it in that row's `notes`.
- Fields per entry:

```yaml
- id: CAP-A-001
  section: A
  section_title: Crawl engine
  title: HTTP and HTTPS crawling        # checklist wording, verbatim, for checklist rows
  origin: checklist                     # checklist | extra
  checklist_row: 1                      # null for extras
  description: null                     # one sentence, required for extras
  discovered_in: []                     # extras only: list of source refs (see format below)
  notes: null
```

At the end of the file, include a section listing any candidate extras you rejected as duplicates, with the checklist row they map to.

### 2. `finding-schema.yaml`: the finding format

A written definition (YAML with comments) of one finding record, plus every allowed enum value with a one-line definition. Use exactly these fields:

```yaml
- id: F-0001                     # sequential, assigned at merge time
  capability: CAP-A-001          # must exist in capabilities.yaml
  statement: >-                  # one line: what is wrong or missing
  gap_type: unwired              # enum below
  layers: [engine, wiring, api, ui]   # subset; which layers are affected
  severity: P1                   # enum below
  evidence:                      # code evidence; empty list allowed only for spec_gap / design_decision
    - path: app/src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs
      symbol: AuditExecutionEngine.ExecuteAsync
      note: RenderedDocument hardcoded to null
  sources:                       # every audit document that mentions this finding
    - doc: UNDERBUILD-AUDIT.md
      locator: "section 4, P0 #3"     # section/heading/row/table locator
      source_id: null            # the document's own ID if it has one (e.g. GAP-04, CRAWL-001)
      claim: >-                  # what that document states, paraphrased in one line
  status: extracted              # enum below
  conflict: null                 # when sources disagree: one line describing the disagreement
  decision_needed: false         # true when the item needs a product decision from the owner
  flow: null                     # end-to-end flow ID, filled in a later step
  notes: null
```

Enums to define:

- `gap_type`: `unwired` (code exists but is not registered or invoked in the running product), `missing_ui` (works via engine/API but no reachable UI), `missing_engine` (no implementation), `spec_gap` (not specified, needs adding to the spec), `design_decision` (a product choice must be made before building), `defect` (implemented but behaves incorrectly).
- `layers`: `engine`, `wiring`, `api`, `ui`.
- `severity`: `P0` (product-breaking: a core workflow cannot be completed or results are wrong), `P1` (major capability missing or misleading), `P2` (capability degraded or incomplete), `P3` (minor or polish). Check how `UNDERBUILD-AUDIT.md` uses P0/P1 and keep the definitions consistent with it.
- `status`: `extracted` (copied from one source, unmerged), `merged` (deduplicated across sources), `conflict` (sources disagree, unresolved), `verified` (confirmed against code by an independent check), `rejected` (shown to be wrong or not a gap).

Also include a short **source document catalogue** in this file: every audit document in `audit-work`, its short name for use in `sources.doc`, and its own ID scheme and locator convention (for example "ux-api-gap-audit.md uses GAP-nn; locate by GAP ID"). Inspect each document to fill this in accurately.

### 3. `findings.yaml`: the empty register

A file containing `findings: []` and a header comment pointing to the schema. No findings.

### 4. `validate_register.py`

A Python 3 script that:

- loads `capabilities.yaml`, `finding-schema.yaml` enums and `findings.yaml`;
- checks capability IDs are unique and correctly formatted, and that all 213 checklist rows are present exactly once;
- checks every finding: required fields present, enums valid, `capability` exists, finding IDs unique, `evidence` non-empty unless `gap_type` is `spec_gap` or `design_decision`, at least one entry in `sources`, `conflict` set when `status` is `conflict`;
- prints a clear pass/fail report and exits non-zero on failure.

Use PyYAML. Check whether it is installed (`python -c "import yaml"`). If it is missing, install it with `python -m pip install --user pyyaml` and say so in your final report.

### 5. `render_register.py`

Generates `register.md` from the YAML files:

- summary counts by severity, gap type, status and section; count of `decision_needed` items and conflicts;
- one section per capability section, listing each capability with its findings (ID, severity, gap type, layers, statement, evidence paths, source docs);
- a separate "Decisions needed" list and a "Conflicts" list;
- capabilities with no findings listed compactly at the end of each section.

Run it once on the empty register to prove it works, and commit nothing.

### 6. `README.md`

Short explanation of the files, the ID rules, the enum meanings, and the consolidation process that will follow:

1. extract per source document;
2. merge by capability ID;
3. verify conflicts and high-severity items against code;
4. separate decisions from build work;
5. map build work to end-to-end flows.

Include the precedence rule: code evidence beats any audit's claim; a newer audit beats an older one only if it cites code.

## Done means

- All six deliverables exist in `audit-work\register\`.
- `validate_register.py` passes on the empty register and confirms 213 checklist capabilities.
- `render_register.py` produces `register.md`.
- No other file anywhere was changed.

Reply with:

- the count of checklist and extra capabilities;
- the extras grouped by section, with a one-line description and source for each;
- the rejected-duplicate list;
- the source document catalogue;
- any judgement calls you made.
