# Crawl Limits and AIMD Politeness

CrawlForge ships with **no artificial crawl ceilings**: every limit defaults to
Unlimited, and nothing in the product consults a license, account, tier, or
entitlement — those concepts do not exist. What keeps a crawl well-behaved is
not a paywall but a politeness system: robots.txt compliance, request spacing,
and an **AIMD auto-adaptive controller** that continuously tunes the request
rate and concurrency for every host based on how that host actually responds.

This guide explains how limits resolve, what each parameter does, and how to
read — and steer — the AIMD controller while an audit runs.

> **Note:** Everything described here is local. Limits resolve from local
> configuration only, the controller learns only from responses to your own
> crawl requests, and no telemetry or remote service participates in any way.

## How limits resolve

A single **limits resolver** produces the effective crawl configuration for
every audit. Resolution is a pure local merge, in this priority order:

1. **Per-audit override** — values you set when starting this audit.
2. **Project default** — values saved on the project.
3. **Application default** — values saved in application settings.
4. **Built-in unlimited profile** — the shipped maximum configuration.

The first layer that supplies a value wins; layers below it are ignored for
that field. A field left unset means "inherit from the next layer down".

Two invariants are worth understanding before you change anything:

- **Unlimited is a real state, not a big number.** Internally, unlimited is
  represented by the absence of a value — never by a magic large number that
  could overflow arithmetic. An unlimited audit genuinely has no ceiling.
- **Running audits keep their snapshot.** Effective values are validated
  before an audit starts, and the audit keeps the limit snapshot it started
  with. Changes you make afterwards apply to *subsequent* audits, never to one
  already in flight.

### Validation

Invalid overrides are rejected before the audit starts, with a descriptive
local error that names the layer, the field, and the rule that was violated
(for example, a concurrency value above the safety cap, or a zero timeout).
Nothing fails silently mid-crawl because of a bad limit value.

## The limit parameters

The table below lists every parameter in the limits subsystem, its meaning,
and the built-in default. "Unlimited" means the ceiling is absent unless you
set it.

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Total page ceiling | Unlimited | Positive integer, or unlimited | Maximum URLs fetched in one audit. |
| Per-domain page ceiling | Unlimited | Positive integer, or unlimited | Counted per registered domain by default; per-host counting is selectable. |
| URL throughput ceiling | Unlimited | Positive number when set | Maximum URLs started per second, globally or per host (per host by default). Politeness rules still apply when unset. |
| Worker concurrency | Scales with logical processor count | Positive integer within the safety cap | Simultaneous fetch workers. The shipped profile uses 2× the logical processor count, capped at 64. |
| Crawl depth ceiling | Unlimited | Non-negative integer | Maximum link hops from any seed URL. `0` fetches only the seeds. |
| Request timeout | 30 seconds | Positive duration | Per-request deadline. |
| Retry budget | 2 | Non-negative integer | Retries per URL on transient failure, with exponential backoff. |
| Redirect hop ceiling | 5 | Positive integer | Maximum redirects followed for one URL; every hop is recorded. |
| Response size ceiling | 16 MiB | Positive size | Maximum bytes accepted per response. |
| Audit duration ceiling | Unlimited | Positive duration | Optional wall-clock cap for the whole audit. |

> **Note:** The timeout, retry budget, redirect hops, and response size are
> generous fixed operational defaults chosen so ordinary sites crawl cleanly
> out of the box. The per-domain ceiling, retry budget, redirect hop ceiling,
> and response size ceiling carry their built-in defaults in this version;
> the per-audit override surface below is what you can set explicitly.

### Per-audit override fields

When you start an audit (`POST /api/audits`), you may pass any subset of these
override fields alongside the configuration reference. Each accepts a value
or stays unset to inherit:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| `maxUrls` | Inherited | Positive integer | Total page ceiling for this audit. |
| `maxDepth` | Inherited | Non-negative integer | Depth ceiling; `0` = seeds only. |
| `maxDurationSeconds` | Inherited | Positive integer (seconds) | Wall-clock cap. |
| `maxConcurrency` | Inherited | Positive integer within the safety cap | Worker concurrency for this audit. |
| `requestsPerSecond` | Inherited | Positive number | Throughput ceiling; unset = unlimited (AIMD still adapts, see below). |
| `fetchTimeoutSeconds` | Inherited | Positive integer (seconds) | Per-request deadline. |

The same fields are available as project defaults and application defaults;
only the layer differs.

> **Tip:** For a quick preview rather than a full run, use **sample-crawl
> mode** in the audit configuration: a depth/page-bounded sampling run through
> the exact same pipeline and limits resolution. It is the safest way to
> probe an unfamiliar site before committing to a full audit.

## Politeness basics (independent of limits)

Politeness applies even when every ceiling is Unlimited:

- **robots.txt is fetched, cached per host, and obeyed** — allow/disallow
  rules and `crawl-delay` directives when present. Sitemaps declared in
  robots.txt are discovered automatically. A robots compliance toggle and a
  virtual robots override (a user-supplied robots.txt body used in place of
  the live one for this audit) are available in the audit's *Politeness
  options*.
- **Request spacing.** When a throughput ceiling is set, it is enforced by
  token-bucket-style request spacing. When unset, per-host concurrency is
  still bounded adaptively so a single origin is never overwhelmed — that
  adaptive bound is exactly what the AIMD controller provides.
- **Backoff.** Rate-limit and server-error responses trigger exponential
  backoff; explicit `Retry-After` hints are honored when present.
- **Identifiable user-agent.** Requests carry the product name and version,
  with optional contact detail, configurable by you.

> **Warning:** A `crawl-delay` directive in robots.txt is an inviolable floor
> on request spacing. No setting — including AIMD — may request faster than
> the delay the site declares.

## AIMD: what it is and why it exists

Static rate limits have two failure modes: set them low and you under-utilize
fast servers; set them high and you overwhelm fragile origins *before* any
HTTP 429/503 error appears. The **AIMD controller** solves this by adapting
continuously, **per host**, from observed response health and latency.

AIMD stands for **Additive Increase / Multiplicative Decrease** — a classic
congestion-control pattern, in plain operational terms:

- **Additive increase:** while a host answers cleanly and quickly, the
  controller nudges the request rate up by a small fixed step. Speed is
  earned gradually, never assumed.
- **Multiplicative decrease:** the moment a host shows strain, the controller
  *halves* the rate immediately. Backing off is fast and decisive; recovery
  is slow and cautious.

The controller sits between politeness spacing and worker dispatch, on each
per-host sub-queue. It **never exceeds** your resolved throughput ceiling or
worker concurrency ceiling — it only adapts within them.

### What the controller watches

For every fetch, the controller measures **TTFB** (time to first byte — the
time from request start to the first response byte). In rendering mode, the
document request's TTFB is used. It also classifies response health:

| Observation | Classification |
|---|---|
| 2xx/3xx without a challenge match | Healthy — counts toward increase streaks. |
| HTTP 429 or 503 | Congestion signal — immediate decrease. |
| WAF challenge detected on a 200 OK response (`waf-challenge`) | Congestion signal — immediate decrease. |
| Other errors | Handled by the normal retry/backoff path; they do not count toward increase streaks. |

From healthy samples it maintains a **rolling TTFB baseline** per host — an
exponentially weighted moving average (smoothing factor λ = 0.2) established
over a warm-up of 10 samples. Two protections keep the baseline honest:
samples that triggered a decrease never update it, and the baseline is frozen
during cooldown.

### The parameters

All parameters are local configuration, merged through the same priority
order as limits, overridable per audit via the *Politeness options* input, and
validated before the audit starts (a running audit keeps its snapshot). The
audit configuration form exposes them as an AIMD parameter group with
client-side validation.

| Field | Default | Bounds | Notes |
|---|---|---|---|
| AIMD enabled | On | On/Off | Off reverts exactly to static politeness behavior. Per audit or globally. |
| Initial rate (R₀) | 1 req/s | Positive number | Conservative starting rate per host. |
| Additive step (α) | 0.5 req/s | Positive number | Linear increase per qualified success window. |
| Multiplicative factor (β) | 0.5 | Between 0 and 1 | Immediate rate halving on congestion. |
| Rate floor (R_min) | 0.1 req/s | Positive number | The effective rate never drops below this (≈ 1 request per 10 s). |
| Adaptive ceiling (R_max-auto) | 10 req/s | Positive number | Used only when the resolved throughput ceiling is Unlimited. |
| Success streak (K) | 5 | Positive integer | Consecutive healthy responses required per increase. |
| TTFB absolute threshold (T_abs) | 800 ms | Positive number | Every streak sample must be at or below this to qualify for an increase. |
| Latency multiplier (κ) | 2.5 | Positive number | Decrease trigger: TTFB above κ × the rolling baseline. |
| Cooldown (D) | 10 s | Positive duration | No increases for this duration after a decrease. |
| Other-5xx decrease | Off | On/Off | Optional extension of decrease triggers to 5xx beyond 429/503. |

### How increase works

The controller raises a host's rate by **0.5 req/s** when all of these hold:

1. **5 consecutive responses** from that host were healthy.
2. **Every sample in the streak** had TTFB ≤ 800 ms.
3. **No cooldown is active.**

The new rate is capped at the host's effective ceiling: your resolved
per-host throughput ceiling when set, otherwise the adaptive ceiling of
10 req/s — and additionally never above `1 / crawl-delay` when robots.txt
declares a delay. When your throughput ceiling is *global*, the scheduler
also ensures the sum of all active host rates never exceeds it. After an
increase, the streak counter resets and the climb starts again.

### How decrease works

Any one of these triggers an **immediate** decrease:

- TTFB rises above **2.5× the rolling baseline** (a latency spike — the site
  is struggling even though it still returns 200).
- A **WAF soft-block** is detected (a challenge page served with 200 OK).
- **HTTP 429** (Too Many Requests).
- **HTTP 503** (Service Unavailable).
- Other 5xx responses, only if you enabled the *Other-5xx decrease* option.

On a decrease, the rate is halved (but never below the 0.1 req/s floor), the
host's concurrency is halved with a minimum of 1, the success streak resets,
and a **10-second cooldown** begins during which no increases are allowed.

**Retry-After interplay:** when a response carries an explicit `Retry-After`
hint, the host sub-queue pauses for the longer of the hint and the cooldown
(10 s). The rate decrease still applies — the hint controls *when* requests
resume, not *how fast* they resume.

**Concurrency coupling:** a host's effective concurrency tracks its rate and
observed full-response latency, clamped to at least 1 and to a fair share of
the total worker concurrency across active hosts. The sum of all host
concurrencies never exceeds your resolved worker concurrency ceiling.

**Cooldown expiry:** increases resume, and the baseline re-warms from new
healthy samples. Given the same response sequence and parameters, the whole
adjustment sequence is reproducible — the controller is deterministic, not
random.

> **Tip:** The asymmetry is the point. A site that answers five fast, clean
> responses earns +0.5 req/s; one strained response costs half the current
> rate. Over a long crawl this converges on the fastest rate each individual
> host can sustain — fast hosts speed up, fragile hosts settle low, and you
> configured none of it per site.

## What you see while an audit runs

- **Live per-host throttle panel** (in the report window): an effective-rate
  sparkline with the TTFB baseline overlaid, trigger badges, and a virtualized
  adjustment log.
- **Dashboard card:** the throttled-host count during running audits. The
  live progress stream (`crawl-progress`) also carries the throttled-host
  count and the mean effective rate.
- **Adjustment events:** every adjustment emits a `host-throttle` hub event
  (audit, host, trigger, rate before/after, concurrency before/after, TTFB
  sample, rolling baseline), coalesced to at most one message per second per
  host. Trigger labels you will see in the log:

| Trigger | Meaning |
|---|---|
| `additive-increase` | A qualified success window raised the rate. |
| `latency-spike` | TTFB exceeded κ × baseline. |
| `waf-soft-block` | A WAF challenge page was detected. |
| `http-429` | The host returned Too Many Requests. |
| `http-503` | The host returned Service Unavailable. |
| `retry-after` | An explicit Retry-After hint paused the host. |
| `cooldown-expiry` | The cooldown ended; increases may resume. |
| `manual-override` | You pinned or cleared a manual rate. |

### Reading the state via the API

- `GET /api/audits/{id}/hosts` — current per-host controller state (effective
  rate, concurrency, smoothed TTFB, adjustment count, last adjustment time,
  any manual pin) and adjustment summaries. Add `includeHistory=true` for
  recent events with pagination.
- The same state is available read-only through the local MCP interface as
  the `host-throttle` resource (audit reference plus optional host filter).

## Pinning a manual rate

Sometimes you know better than the controller — for example, a site owner
has told you their infrastructure tolerates exactly 2 req/s.

1. Open the per-host throttle panel for the running audit.
2. Use the **manual pin control** on the host and set the rate (or call
   `POST /api/audits/{id}/hosts/{host}/override` with the rate).
3. The host's AIMD adaptation is **suspended** while pinned, and the panel
   shows a clear visual state marking the suspension. A `manual-override`
   event is recorded.
4. Clear the pin (same control, or the override endpoint with a clear
   action) to hand the host back to AIMD.

> **Warning:** A pin overrides adaptation, not politeness. Robots
> `crawl-delay` and your resolved ceilings still bound the host. Pinning a
> rate above what a site can handle is your responsibility — the controller
> will not rescue a pinned host from 429s.

## What happens to throttle state on pause, resume, and crash

- **Pause:** the full per-host controller state (rate, concurrency, baseline,
  streak, cooldown-until, last-adjustment metadata) is serialized into the
  audit's checkpoint payload. **Resume rehydrates it exactly** — the crawl
  continues with the same politeness posture it had when you paused.
- **Crash recovery:** state is re-derived *conservatively* — every host
  restarts at the initial rate (1 req/s) with a fresh warm-up. After an
  unexpected exit, CrawlForge deliberately re-earns speed rather than
  assuming the previous rate is still safe.
- **Completion:** the final per-host state is summarized into the audit's
  metrics under the `host-throttle` metric key, so the throttle story of a
  finished audit remains inspectable.

Adjustment history is bounded: the latest **500 events per host per audit**
are retained by default (configurable), and older events are pruned with the
standard retention policy.

## WAF soft-blocks and the block-rate alert

A modern WAF (Cloudflare, DataDome, Akamai, Imperva, PerimeterX) often serves
a challenge page with a perfectly normal **200 OK** status. CrawlForge
inspects every response at receipt and classifies confirmed challenge pages
as `blocked` with sub-classification `waf-challenge` — they are never counted
as healthy fetched pages, never retried, and never contribute links. Every
such detection is an immediate AIMD decrease for the host.

If the share of WAF-blocked fetches grows, a site-wide alert fires:
**Warning at ≥ 1%** and **Error at ≥ 5%** of attempted fetches (both
thresholds configurable; the Error level requires a minimum sample of 20
attempts). The dashboard shows a persistent banner while the alert is active.

> **Note:** Detection is diagnostic only. CrawlForge never attempts to bypass
> a challenge. The remediation path is to verify crawler access with the site
> owner, or adjust scope or supplied credentials in the audit configuration.

## Practical recipes

### Crawl a fragile shared-hosting site gently

1. Start the audit with `requestsPerSecond` set to a modest value (for
   example `1`) and `maxConcurrency` low (for example `2`).
2. Leave AIMD enabled — it will back off further on any strain and recover
   cautiously if the site proves healthier than expected.
3. Watch the throttle panel; if the host keeps hitting `http-429`, pin a
   lower manual rate instead of letting the controller oscillate.

### Crawl your own fast infrastructure at full speed

1. Leave all ceilings Unlimited (the default).
2. AIMD starts each host at 1 req/s and climbs by 0.5 req/s per clean window
   up to the adaptive ceiling of 10 req/s per host. Raise the adaptive
   ceiling in the AIMD parameter group if your own origin can take more.
3. Remember the worker concurrency ceiling still applies globally; raise
   `maxConcurrency` within the safety cap if you want more parallel hosts.

### Reproduce a static, predictable crawl

1. Turn AIMD **off** (per audit or globally).
2. Set an explicit `requestsPerSecond`. Behavior reverts exactly to static
   token-bucket spacing — useful for benchmarking or when a site owner has
   mandated a fixed rate.

## Related guides

- [Lifecycle and checkpointing](./lifecycle-and-checkpointing.md) — pause,
  resume, and how throttle state survives restarts.
- [Scheduled audits](./scheduled-audits.md) — recurring runs use the same
  limits resolution and AIMD behavior.
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)
  — where project-level limit defaults live.

[← Documentation portal](../README.md)
