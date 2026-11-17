# Task: Promote the CrawlForge architecture to living master, tag it with capability IDs, and draft change records for the owner's decisions

You are working on the specification of CrawlForge, a desktop website crawler and SEO auditor. Your job is to fold the newly agreed capability spine and owner decisions into `E:\Crawlforge\architecture` **faithfully**, following the change-record protocol. You do not touch application code.

`E:\Crawlforge` is a git repository (tag `baseline-v1`). Commit as instructed below. Do not push anywhere. End every commit message with:

```
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
```

## Read first (in this order)

1. `E:\Crawlforge\audit-work\SOURCE-OF-TRUTH-LOOP.md`: the operating rules. Sections 4, 5 and 6 define exactly what you are doing. Follow them.
2. `E:\Crawlforge\audit-work\decisions-log.md`: owner decisions D-001 to D-015.
3. `E:\Crawlforge\audit-work\register\capabilities.yaml` (275 capabilities: 213 checklist rows plus 62 extras in sections A to L) and `register\README.md`.
4. `E:\Crawlforge\audit-work\project-configuration-review.md`: the detailed rationale behind the configuration, rendering and site-type decisions (sections 3, 4, 5).
5. `E:\Crawlforge\architecture\README.md` and `FIDELITY-AUDIT-REPORT.md`, then every domain file under `architecture\01-...` to `07-...`.

## Hard rules

- **Never change the meaning of existing requirement text in this task.** Part A and Part B only add tags, metadata and README text. All requirement changes happen as **draft** change records in Part C, which the owner must approve before anyone applies them.
- **Do not invent requirements.** Draft CR text must come from the decisions log, the capability descriptions and notes, or the configuration review. Where those sources leave a detail open (for example default values, exact limits, UI layout), write it as an explicit open question inside the CR. Do not fill it in.
- **Keep existing section numbering** and existing `§n` / `FS-CRAWL-002 §n` references valid.
- Do not edit `app\`, `buildplan\`, or anything in `audit-work\` except where stated.
- Exclude `bin`, `obj`, `node_modules`, `dist` from all searches.

## Part A: CR-001, promote the architecture to living master (apply)

1. Create `architecture\changes\` with `INDEX.md` (table: CR, title, type, status, decisions, capabilities, files, approved date) and `CR-001.md`.
2. `git mv` the two root master documents into `architecture\baseline\`: `functional-specification.md` (FS-CRAWL-001 v1.4) and `crawlforge-extensions-and-ui-stack.md` (FS-CRAWL-002 v1.1). Do not change their content. Add `architecture\baseline\README.md`: "Historical baseline. Superseded by the living architecture from v2.0. Do not edit."
3. Update path references to those two files **inside `architecture\` and `audit-work\` only**. References in `buildplan\AGENT-INSTRUCTIONS.md` and `build_plan_orchestration_prompt.md` must **not** be edited; list them in CR-001 under "Downstream references to update in the build-plan extension".
4. Update `architecture\README.md` document control: identifier `FS-CRAWL-ARCH`, version **2.0**, status **Master (living)**, and replace the "immutable sources / verbatim and unabridged" statements with the wording given in SOURCE-OF-TRUTH-LOOP.md section 4 step 2. Add a short "Change protocol" section summarising section 5 of the loop document and pointing to `changes\INDEX.md`. Keep the rest of the README, including the existing source-section traceability matrix.
5. Mark CR-001 `applied` (type: clarification, approved by the owner on 2026-11-17 as part of adopting the loop) and commit: `CR-001: promote architecture to living master v2.0`.

## Part B: CR-002, capability traceability tags (apply)

1. For every capability in `capabilities.yaml`, find the architecture section(s) that specify it. Use the capability title, description, notes and `discovered_in` sources, and the checklist wording. Be precise: tag the most specific section (heading level `###` or `####`) that actually specifies the behaviour, not a whole domain file.
2. Directly under each tagged heading insert one traceability line, in this exact format, and nothing else:

   `> Capabilities: CAP-A-006, CAP-A-X21`

   (Decisions and CR references are added to this line later, when CRs that touch the section are applied.)
3. Capabilities with **no** specifying section are not tagged anywhere. Record each one in CR-002 with a reason: `spec gap` (needs new text; name the draft CR in Part C that will add it) or `out of scope` (decisions D-006, D-009, D-013: rows 189, 191, 187).
4. Write `audit-work\register\check_traceability.py` (Python 3, PyYAML) that reads `capabilities.yaml` and every `.md` under `architecture\` (excluding `baseline\`), and reports: capabilities tagged nowhere; tags referencing unknown capability IDs; malformed traceability lines; a generated capability-to-section index written to `architecture\TRACEABILITY.md` (deterministic output, sorted by capability ID, with file and heading for each). Capabilities listed in CR-002 as spec gaps or out of scope are reported separately, not as failures.
5. Run it until it reports zero unknown IDs and zero malformed lines, and every untagged capability is accounted for in CR-002.
6. Mark CR-002 `applied` (type: traceability, approved 2026-11-17) and commit: `CR-002: capability traceability tags and index`.

## Part C: draft change records for the decisions (do NOT apply)

Write draft CRs, status `draft`, one per coherent topic, using the placement table in SOURCE-OF-TRUTH-LOOP.md section 6 as the guide. Expected set (merge or split only with a stated reason):

| Draft | Topic | Main inputs |
|---|---|---|
| CR-003 | Network boundary amendment: user-connected Google APIs (GSC, GA4, PageSpeed Insights), user-triggered update channel | D-005, D-012, D-010 |
| CR-004 | Open source, AGPL-3.0, CLA; reconcile "no licences" wording | D-010, D-011 |
| CR-005 | Capture modes (non-JS, JS, dual, adaptive) | D-001, CAP-A-X09 |
| CR-006 | Direct CDP rendering layer replacing PuppeteerSharp | D-002, CAP-A-X10 and related |
| CR-007 | Pinned bundled browser and browser-path setting | D-003, CAP-A-X11 |
| CR-008 | Crawl behaviour additions: cookie jar, crawler traps, UA comparison, directive-handling toggles, resource/external scope toggles, URL rewrite and host mapping | CAP-A-X06, X12, X13, X14, X21, X22 |
| CR-009 | Site-type handling: hash routes, postback detection, non-crawlable navigation, client-side redirects, service-worker bypass, cookieless-session normalisation, ViewState weight, rendered-first soft 404, late-meta severity | config review §5; CAP-A-X15 to X18, CAP-B-X03, CAP-B-X05, CAP-C-X05 |
| CR-010 | Consent-state rendering | CAP-A-X19 |
| CR-011 | MCP as AI channel: steering tools, summary-first paginated responses, audit status/completion tools, agent data contract (CSV, JSON Lines, Markdown, read-only SQLite snapshot) | D-008, D-013, D-014; rows 185, 186 |
| CR-012 | Projects and configuration (section K): configuration editor, per-configuration analysis toggles, per-project thresholds, client context, retention | CAP-K-X01 to X05 |
| CR-013 | New domain `08-integrations`: GSC, GA4, PageSpeed Insights (OAuth bring-your-own-client behind an abstraction, per-project property binding, sync, local cache, offline CSV fallback) | D-005, D-012, D-009; CAP-A-X08 |
| CR-014 | New domain `09-distribution`: installer, Windows code signing, update channel, opt-in crash reporting, licence audit before release | D-009, D-010, D-011 |
| CR-015 | Fixture test site and end-to-end acceptance; new acceptance criteria from #49 | D-007 |
| CR-016 | Out-of-scope records: email reports, webhooks, multi-user | D-006, D-009, D-013 |

Each draft CR file must contain:

- header: ID, title, type (`addition` / `amendment`), status `draft`, decisions, capability IDs, target files and sections;
- **Rationale**: one short paragraph citing the sources;
- **Proposed text**: the exact new sections or replacement text, written in the same normative style as the surrounding architecture (requirement tables, "must" statements, numbered subsections). For amendments, quote the current text being replaced in full, then the replacement;
- **Traceability**: the capability IDs each new or amended section will carry;
- **Open questions**: every detail the sources leave undecided;
- **Impact**: other sections, contracts (REST, hub, MCP, schema) and acceptance criteria affected.

Add all drafts to `changes\INDEX.md` as `draft`. Commit once: `CR-003 to CR-016: draft change records for owner review`.

## Done means

- CR-001 and CR-002 applied and committed; root masters moved to `architecture\baseline\`; README at v2.0.
- Every capability is either tagged in the architecture or listed in CR-002 as spec gap (with its draft CR) or out of scope.
- `check_traceability.py` passes and `architecture\TRACEABILITY.md` is generated.
- Draft CRs CR-003 to CR-016 written, indexed, committed, **not applied**.
- No existing requirement text changed in meaning; `git diff baseline-v1 -- architecture` shows only moves, README changes, traceability lines and new files.

## Final reply

Reply with: counts of capabilities tagged, spec gaps and out-of-scope; the list of spec gaps with their draft CR; downstream references found in the build plan; for each draft CR a two-line summary plus its open questions count; and any judgement calls you made.
