# audit-work: what is authoritative

This folder holds the post-build audit work, the operating rules and the capability register. **The specification itself is `architecture/`; the build plan is `buildplan/`.** Nothing here overrides either.

## Authoritative (current)

| Path | What it is |
|---|---|
| `SOURCE-OF-TRUTH-LOOP.md` | Operating rules: how decisions, register, architecture, build plan and app relate. Every session follows it. |
| `decisions-log.md` | Owner decisions D-001 onward. |
| `register/` | Capability spine (`capabilities.yaml`), untagged capabilities, finding schema, `check_traceability.py`, `check_buildplan.py`. |
| `reports/runtime-probe-2/` | **Strongest evidence of the as-built app**: clean probe through normal entry points against the fixture site (2026-11-17). |
| `reports/hotfix-01/` | HOTFIX-01 report and screenshots (audits run, run view stays up). |
| `reports/UNDERBUILD-AUDIT.md` | Code-level gap register (G01 to G29). Still useful for where gaps sit in the code; superseded by probe-2 wherever they differ. |

## Prompts

`prompts/` holds the prompts used to drive Claude Code sessions (register spine, architecture fold-in, runtime probe, HOTFIX-01, build plan v2.0). Kept for reproducibility.

## History (superseded, use as leads only)

`history/` holds earlier audits and inventories (01 to 05, gap analyses, UX/API gap audit, functionality checklist and results, configuration review) and the first runtime probe (`runtime-probe-1`, which used diagnostic database patches). Their claims were overtaken by architecture v2.0 and probe-2. **Do not treat anything in `history/` as proof of what the app does.**

Evidence order: runtime probe > code reading > audit claims.
