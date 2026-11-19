# PHASE-09 ADDENDUM — Criterion #56 proven through UI-04 edits (phase-10 step 0)

- **Date:** 2026-11-19 (walk 20261119T213720Z UTC / 23:37 local)
- **Coordinator:** lead (phase-10 prompt step 0)
- **Verdict: PASS — criterion #56 stays `completed`.** All 17 walk checks OK; harness exit 0.
- **Evidence root:** `app/tests/fixture-acceptance/results/20261119T213720Z/`
  (`ui-edit-56.json`, `audit-41-data.json`, `audit-42-data.json`,
  `fixture-request-log.json`, `summary.md`, `app-shutdown.json`, screenshots below)

## Why this addendum exists

Phase 09 proved criterion #56 ("a configuration **created and edited in the UI**
changes audit behaviour") with a configuration created through the UI dialog but
whose limits were bounded by a REST `PUT` (UI-04 did not exist yet), and the
cookie jar was never toggled in the editor. The phase-10 prompt step 0 closes
that gap: every configuration change below goes through **UI-04 editor controls
over CDP only**. The harness performs **no REST writes**; REST is used read-only
for read-back verification and audit polling (the same inspection the UI does).
The UI's own save `PUT /api/configurations/{id}` is the mechanism under test.

## Harness extension

`app/tests/fixture-acceptance/run_fixture_acceptance.py` gains `--ui-edit-56`
(function `ui_edit_56_walk`): UI New-project dialog → project row → UI-03
configurations list → UI-04 editor → control edits → UI save (PUT observed on
the CDP network domain + REST read-back) → hub **Start audit** button → poll →
verdicts. Two runs on one project/configuration:

- **Run A** — cookie jar **OFF** (`config-cookie-jar` Switch), max URLs **50**
  (`config-limit-max-urls-unlimited` off → `config-limit-max-urls-override`
  filled "50"), save, start.
- **Run B** — cookie jar **ON**, max URLs back to **Unlimited**, plus a
  duration ceiling of **300 s** set through `config-limit-max-duration-*`
  (the fixture's calendar trap expands without bound; the ceiling is itself a
  UI-04 edit and keeps the walk measurable — run A's 50-URL cap already proved
  the limit control in the binding direction). Save, start.

Project **29** ("Fixture harness 56 20261119T213720Z"), configuration **33**.

## Run A — jar OFF, maxUrls 50 (audit 41)

| Check | Result |
|---|---|
| Editor jar switch before→after | true→false via real input click |
| Editor max-urls field | Unlimited off, override value "50" |
| UI save PUT observed + REST read-back | `cookieJar.enabled=false`, `limits.maxUrls=50` |
| Audit state | completed (20.2 s poll) |
| **maxUrls honoured** | **counters.fetched = 50** (54 url rows: 50 fetched + 4 discovered-not-fetched) |
| cookie-gate row | **statusCode 302** (not 200) |
| Loop finding | `redirects.loop`, severity error, finding 43181 on url 6961: "Redirect loop detected at 'http://127.0.0.1:47311/cookie-gate/'." |
| cookie-gate trap verdict | `partial` — **not pass** (jar off must not pass; `t_cookie_gate` requires 200) |
| Safety | `nonGet == []` |

Screenshots: `UI-04-A-auth-jar-off.png`, `UI-04-A-limits-50.png`,
`UI-10-A-completed.png`.

## Run B — jar ON, Unlimited (audit 42)

| Check | Result |
|---|---|
| Editor jar switch before→after | false→true via real input click |
| Editor max-urls field | Unlimited on (override cleared) |
| Editor duration field | Unlimited off, override value "300" |
| UI save PUT observed + REST read-back | `cookieJar.enabled=true`, `limits.maxUrls=null`, `limits.maxDurationSeconds=300` |
| Audit state | completed (324.2 s poll; duration ceiling honoured) |
| **URL cap lifted** | **counters.fetched = 613** (> 50 proves the Unlimited flip took effect) |
| cookie-gate row | **statusCode 200** |
| cookie-gate trap verdict | **pass** |
| Safety | `nonGet == []` |

Screenshots: `UI-04-B-auth-jar-on.png`, `UI-04-B-limits-unlimited.png`,
`UI-04-B-limits-duration-300.png`, `UI-10-B-completed.png`.

## Behaviour flip (the criterion)

Same project, same configuration, same fixture, same UI start button — the only
differences are the UI-04 edits:

| | Run A (jar off, cap 50) | Run B (jar on, unlimited) |
|---|---|---|
| cookie-gate final status | 302 self-redirect loop | 200 "Cookie gate passed" |
| `redirects.loop` finding | present (error) | absent for cookie-gate |
| trap verdict | partial (not pass) | **pass** |
| counters.fetched | **50** (cap enforced) | **613** (cap lifted) |

A configuration created **and edited** in the UI changes audit behaviour.
Criterion #56 is proven through the UI; no index change is needed.

## E2E helper tightening (same commit)

Commit `2456592` had relaxed hub/REST `auditId` parsing during the G-G string-id
transition. The locked contract carries `auditId` as a **string EntityId**; the
tolerance is now removed:

- `app/tests/CrawlForge.E2E.Tests/HubSequencingE2ETests.cs` — `PayloadAuditId`
  accepts **only** `JsonValueKind.String` (numeric producers now fail the
  sequencing assertions instead of being tolerated).
- `app/tests/CrawlForge.E2E.Tests/ListModeE2ETests.cs` — the start-envelope and
  matrix-upload `auditId` parses already used `GetString()`; explicit
  `Assert.Equal(JsonValueKind.String, …)` assertions now lock the kind at both
  sites (step-0 comments).

Verification: `dotnet test tests/CrawlForge.E2E.Tests --filter
"FullyQualifiedName~ListModeE2ETests|FullyQualifiedName~HubSequencingE2ETests"`
→ **Passed! 14/14** (0 failed, 0 skipped).

Observation (not changed, outside step-0 scope): `ReopenBatchWiringTests` and
`AimdHostThrottleE2ETests` received the same transitional string-tolerant parse
in `2456592`; they still tolerate numeric ids. Suggested owner: phase-11
contract-hygiene pass.

## Run history note

The first walk attempt (results `20261119T212202Z`, audits 39/40) proved run A and
the run-B cookie-gate pass, but run B was fully unlimited and outran the 600 s
poll cap on the fixture's infinite calendar trap (1116 fetched, still running).
The walk was corrected to bound run B with a **UI-set** 300 s duration ceiling
and a `fetched > 50` cap-lifted check; the committed harness is that version and
the committed evidence is the clean 17/17 run above. Nothing was hidden: both
runs' records remain in the results tree.

## Commands

```
python app\tests\fixture-acceptance\run_fixture_acceptance.py --ui-edit-56
# EXIT 0 — all 17 checks OK (results/20261119T213720Z)
dotnet test tests/CrawlForge.E2E.Tests/CrawlForge.E2E.Tests.csproj --filter "FullyQualifiedName~ListModeE2ETests|FullyQualifiedName~HubSequencingE2ETests"
# Passed! 14/14
```

App shutdown clean (`app-shutdown.json`: residual [], ports_free true); fixture
stopped; owner data untouched (test project only; app-data backup
`CrawlForge-backup-phase10-20261119-231246` taken before the first run).