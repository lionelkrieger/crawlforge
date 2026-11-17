# CrawlForge: Source-of-Truth Loop

*Operating rules for consolidating the audits, evolving the specification and extending the build plan without drift. Adopted 2026-11-17. Every session working on the register, the architecture or the build plan must follow this document.*

## 1. Why this exists

The first build reported 43/43 workpieces and 48/48 acceptance criteria complete while much of the product was unwired or had no UI. The build plan had become its own source of truth: "done" was measured against the plan, not against the specification or the running product. This loop makes the specification the single master, makes every layer trace back to it by ID, and only lets something count as built when the running product proves it.

## 2. The loop

```
decisions-log.md ─┐
                  ├─▶ register (capabilities + findings) ─▶ architecture (master spec) ─▶ build plan ─▶ app
audit findings ───┘            ▲                                                                     │
                               └──────────── verification against the fixture site ◀────────────────┘
```

Authority flows one way, left to right. Nothing downstream may change anything upstream directly.

| Layer | Location | Role | ID scheme |
|---|---|---|---|
| Decisions | `audit-work/decisions-log.md` | Owner's product decisions | `D-nnn`, open items `O-nnn` |
| Register | `audit-work/register/` | What capabilities exist and what is wrong with each | `CAP-<section>-<row>` / `CAP-<section>-Xnn`, findings `F-nnnn` |
| Architecture | `architecture/` | **The master specification** | Domain files + section numbers; change records `CR-nnn` |
| Build plan | `buildplan/` | How and in what order the spec gets built | Workpieces `WP-<phase>-<nn>`, acceptance criteria `#nn` |
| App | `app/` | The product | n/a |

## 3. Step zero: version control

Nothing in `E:\Crawlforge` is under version control today. Before any architecture edit:

1. Initialise a git repository at `E:\Crawlforge` covering `architecture/`, `buildplan/`, `audit-work/`, the two root master documents and `app/` source (excluding `bin`, `obj`, `node_modules`, `dist`, logs and vendor binaries via `.gitignore`).
2. Commit the current state and tag it `baseline-v1` ("first build, pre-audit").
3. From then on, every change record (section 5) is its own commit, referencing its `CR-nnn`.

## 4. Promoting the architecture to master

Today `architecture/` is a verbatim, read-only decomposition of two immutable root masters (`functional-specification.md` = FS-CRAWL-001 v1.4, `crawlforge-extensions-and-ui-stack.md` = FS-CRAWL-002 v1.1). That arrangement means every spec change would have to be written twice. It is replaced as follows.

1. **Freeze the root masters as baseline.** Move both root master documents to `architecture/baseline/`, unchanged, with a note: "Historical baseline. Superseded by the living architecture from v2.0. Do not edit." They remain the record of original intent.
2. **Declare the architecture the living master.** Update `architecture/README.md` document control: identifier `FS-CRAWL-ARCH`, version **2.0**, status **Master (living)**. Replace the "verbatim and unabridged / immutable sources" statements with: "This hierarchy is the authoritative specification. It was derived losslessly from FS-CRAWL-001 v1.4 and FS-CRAWL-002 v1.1 (see `baseline/`) and is amended only through change records."
3. **Keep existing text and numbering.** Existing `§n` and `FS-CRAWL-002 §n` references stay valid so the first build's code comments and workpieces still resolve. New sections use the new numbering in section 6.
4. **Add capability traceability.** Every spec section gets a traceability line directly under its heading:

   `> Capabilities: CAP-A-006, CAP-A-X21 · Decisions: D-005 · Change records: CR-004`

   The README traceability matrix gains a capability column, so every in-scope capability maps to at least one section and every section lists what it delivers.

## 5. Change protocol (how the architecture is edited)

No one edits architecture text without a change record.

1. **Change record.** Each change is described in `architecture/changes/CR-nnn.md`: what changes, why, which decisions (`D-nnn`) and capabilities/findings (`CAP-...`, `F-nnnn`) drive it, which files and sections it touches, and its type: `addition` (new requirement), `amendment` (replaces existing requirement text), `traceability` (tags only, no requirement change) or `clarification` (no change in meaning).
2. **Amendments are explicit.** Amended text is marked inline: `**[Amended by CR-nnn, D-nnn]**`. The replaced text is not deleted silently; it moves to an "Amendment history" block at the end of that domain file with the CR reference. No requirement may silently contradict another.
3. **Additions** go into the domain file they belong to, under a clear heading, tagged with their CR.
4. **Index.** `architecture/changes/INDEX.md` lists every CR with status (`draft`, `approved`, `applied`) and the owner's approval date. Only `approved` CRs may be applied. The owner approves.
5. **One CR, one commit.**

## 6. Where new material goes

Most register findings need **no spec change**: gaps of type `unwired` and `missing_ui` are things the spec already requires and the build did not deliver. They only receive `traceability` CRs (capability tags on the existing sections).

`spec_gap` and `design_decision` findings, and the decisions log, produce `addition` or `amendment` CRs. Expected placements:

| Topic | Placement |
|---|---|
| Network boundary changes (GSC, GA4, PageSpeed Insights, update channel) | Amendment to `01-foundation-and-governance/03-network-boundary-and-security.md` |
| Open source, AGPL-3.0, CLA; removal of "no licences" wording conflicts | Amendment to `01-foundation-and-governance/01-clean-room-and-exclusions.md` |
| Capture modes, direct CDP layer, pinned browser | Amendment to `03-crawl-engine/03-rendering-and-proxy-routing.md` |
| Cookie jar, crawler traps, UA comparison, hash routes, postbacks, client-side redirects, directive and scope toggles | Additions to `03-crawl-engine/02-frontier-fetching-and-waf.md` and `03-rendering-and-proxy-routing.md` |
| Soft 404 on rendered, ViewState, non-crawlable navigation findings | Additions to `04-audit-and-analysis-engine/02-core-seo-and-indexability-rules.md` |
| Consent-state rendering | Addition to `04-audit-and-analysis-engine/06-ai-geo-and-martech-auditing.md` |
| MCP steering, summary-first responses, audit completion tools, agent data contract | Amendment/addition to `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md` and `01-rest-api-contract.md` |
| Configuration editor, per-project toggles, thresholds, client context, retention (section K) | Additions to `06-frontend-shell-and-ui/` and `03-crawl-engine/` as appropriate |
| GSC / GA4 / PageSpeed Insights integrations | New domain file `08-integrations/01-google-integrations.md` |
| Distribution: installer, code signing, update channel, opt-in crash reporting, licence audit | New domain file `09-distribution/01-packaging-and-release.md` |
| Fixture test site and end-to-end acceptance | Addition to `07-quality-and-verification/`; new acceptance criteria continue from **#49** |

## 7. Build plan rules

1. The build-plan extension is generated **from the architecture**, never from the register directly.
2. Every new workpiece cites: the architecture file and sections it implements, the capability IDs it delivers, and the change records involved.
3. Work is organised around end-to-end flows (for example: create project → configure → HTTP audit → results grid → export), so each workpiece crosses engine, wiring, API and UI.
4. Every new acceptance criterion (#49 onward) names the fixture-site trap pages that prove it.
5. A workpiece is **complete** only when every capability it delivers reaches `verified` in the register, based on an end-to-end run against the fixture site. Unit tests alone never complete a workpiece.
6. The existing amendment protocol (`WP-<phase>-A<n>` / `WP-<phase>-D<n>`) is used for contract and dependency changes.

## 8. Discoveries during the build

If building reveals that the spec is wrong, incomplete or contradictory, the builder **stops that item and raises it**. The fix flows back through the loop: decision (if a product choice is needed) → register finding → CR → architecture → build plan. Builders never change the spec or the plan to match what they built.

## 9. Traceability check

Extend `register/validate_register.py` (or add `check_traceability.py`) to fail when:

- an in-scope capability has no architecture section tagging it;
- an architecture section references a capability ID that does not exist;
- a CR is applied but not approved, or approved but not applied;
- a workpiece cites no capability IDs or no architecture sections;
- a capability is marked built in the build plan but not `verified` in the register;
- an acceptance criterion from #49 onward names no fixture trap.

Run it before every phase gate.

## 10. Order of work

1. Version control (section 3).
2. Register steps 2 to 4: extract findings per audit document, merge, verify against code.
3. Owner resolves any new decisions surfaced by the findings.
4. Promote the architecture (section 4) and apply `traceability` CRs.
5. Write and approve `addition` / `amendment` CRs; apply them.
6. Design the fixture site (as a spec addition in `07-quality-and-verification`).
7. Generate the build-plan extension from the architecture.
8. Build, verify against the fixture site, update the register, repeat.
