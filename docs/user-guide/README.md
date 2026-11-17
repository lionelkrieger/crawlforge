# CrawlForge User Documentation

**CrawlForge** is a standalone, offline-first desktop website crawler and auditor. It crawls a target site, runs a large battery of SEO, accessibility, performance, and content-quality audits, and renders every report locally — at a scale of hundreds of thousands of URLs, with explorers that stay smooth at **500,000+ rows**.

This guide suite is written for **technical SEO consultants, agency digital marketers, and solo web developers**. You do not need to be a programmer to run a full audit; where deeper technical detail exists (endpoints, parameters, schemas), it is presented in tables you can skim or skip.

---

## The local-first promise

CrawlForge is built around a small set of guarantees that never change, no matter which features you enable:

| Guarantee | What it means for you |
|---|---|
| **No accounts, no licenses** | Single-user desktop app. No sign-in, no subscription, no trial, no seat limits. Every feature is available to you. |
| **Zero telemetry** | No usage analytics, no remote crash reporting, no automatic transmission of any diagnostic data. Local logs and crash minidumps exist for *your* troubleshooting — viewable and purgeable by you, never sent anywhere. |
| **Works fully offline** | Every feature works with no network connectivity, except the act of crawling target websites itself and the optional destinations you configure. Reports render entirely from local data. |
| **Your data stays on your machine** | All persistence is local by default (an embedded file-based database). Data leaves the machine only toward services **you** explicitly configure: crawl targets, proxies, export destinations, an optional external database you own. |
| **Loopback-only services** | The backend, real-time hub, metrics endpoint, and optional MCP automation server bind to loopback only. Nothing listens on your network. |
| **No remote control** | No remote feature flags, no cloud relay, no account-bound update feeds, no vendor services of any kind. |
| **Detection, never bypass** | WAF challenge detection is diagnostic. CrawlForge never solves CAPTCHAs, forges tokens, or automates evasion. |

> **Note:** Optional modules that touch the network — export destinations, an external MySQL server, Ollama embeddings (loopback-only), and DNS bot verification for log analysis — are **off by default** and require your explicit configuration or per-use consent.

---

## How to use this documentation

- **Guides are task-oriented.** Each one walks through real workflows with numbered steps, then provides reference tables for exact field names, defaults, and bounds.
- **Callouts** flag things worth attention:
  > **Note:** background context that helps understanding.
  > **Tip:** a faster or safer way to do something.
  > **Warning:** something that can affect your data, your crawl, or a live site.
- **Exact names** — endpoints, fields, settings, and tools — appear in `backticks` so you can match them precisely in the app or API.
- Every guide ends with **Related guides** links and a link back to this portal.

---

## Table of contents

### 1 · Getting started

Set up CrawlForge and choose where your data lives.

| Guide | What you'll learn |
|---|---|
| [Installation & first launch](01-getting-started/installation-and-first-launch.md) | Platform setup, self-contained runtimes, the Server GC performance profile, and what happens on your first (fully offline) launch. |
| [Database configuration](01-getting-started/database-configuration.md) | The default file-based SQLite setup vs. an optional user-owned MySQL server, connection testing, and switching providers safely. |

### 2 · Managing projects & crawls

Define what to crawl and how to reach it.

| Guide | What you'll learn |
|---|---|
| [Creating projects & scopes](02-managing-projects-and-crawls/creating-projects-and-scopes.md) | Seed URLs, host/subdomain scope definitions, inclusion/exclusion rules, and pre-crawl start-URL validation. |
| [List Mode & migration matrix](02-managing-projects-and-crawls/list-mode-and-migration-matrix.md) | Upload URL lists for depth-0 crawls; validate site migrations with Source vs. Expected Target redirect matrices and the 5-tier verdict system. |
| [Proxies & authenticated crawls](02-managing-projects-and-crawls/proxies-and-authenticated-crawls.md) | Upstream and local proxy routing, custom headers/cookies, and signed-request access tokens — with credentials encrypted at rest. |

### 3 · Running & scheduling audits

Control the crawl engine and keep it polite.

| Guide | What you'll learn |
|---|---|
| [Crawl limits & AIMD politeness](03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md) | Unlimited defaults, concurrency caps, robots.txt compliance, and how AIMD automatically adapts request rates to protect target sites. |
| [Lifecycle & checkpointing](03-running-and-scheduling-audits/lifecycle-and-checkpointing.md) | Start, pause, resume, and cancel audits; crash-recovery checkpoints and what survives a restart. |
| [Scheduled audits](03-running-and-scheduling-audits/scheduled-audits.md) | Recurrence rules, local-timezone evaluation, catch-up behavior for asleep machines, and user-authorized completion actions. |

### 4 · Exploring reports & visualizations

Read your results at any scale.

| Guide | What you'll learn |
|---|---|
| [Dashboards & virtualized explorers](04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md) | The Mantine UI, Ctrl+K Spotlight, 500k+ row grids at 60 FPS, multi-column sorting, pinning, instant filtering, and the query builder. |
| [Link graphs & treemaps](04-exploring-reports-and-visualizations/link-graphs-and-treemaps.md) | Navigate WebGL force-directed internal link networks and crawl-depth treemaps; read zone-weighted link equity. |
| [Exports & secure destinations](04-exploring-reports-and-visualizations/exports-and-secure-destinations.md) | Generate Excel, CSV, PDF, and HTML report bundles; configure encrypted-at-rest S3/webhook delivery destinations you own. |

### 5 · Advanced SEO & intelligence modules

Go beyond the core audit.

| Guide | What you'll learn |
|---|---|
| [WAF & soft-block detection](05-advanced-seo-and-intelligence-modules/waf-and-soft-block-detection.md) | How CrawlForge spots Cloudflare/DataDome/Akamai (and other) challenges hidden behind HTTP 200 OK — without getting trapped or false-positiving. |
| [Content scoping & boilerplate stripping](05-advanced-seo-and-intelligence-modules/content-scoping-and-boilerplate-stripping.md) | Include/Exclude CSS/XPath rules and presets that clean word counts, readability, and spelling checks. |
| [N-grams & topical density](05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) | 1/2/3-gram phrase frequencies, site-wide document frequency, keyword cannibalization, and topical gaps. |
| [Semantic internal linking](05-advanced-seo-and-intelligence-modules/semantic-internal-linking.md) | Activate local ONNX or loopback Ollama embeddings; find semantic cannibalization and automated internal-link opportunities. |
| [AI (GEO) readiness](05-advanced-seo-and-intelligence-modules/ai-geo-readiness.md) | Validate `/llms.txt` and `/llms-full.txt`; navigate the AI-bot governance matrix for search vs. RAG vs. training. |
| [MarTech & Consent Mode auditing](05-advanced-seo-and-intelligence-modules/martech-and-consent-mode-auditing.md) | Inventory GTM, GA4, and pixels; inspect post-render `window.dataLayer` and Google Consent Mode v2 states. |
| [Offline server log analyzer](05-advanced-seo-and-intelligence-modules/offline-server-log-analyzer.md) | Import Apache/Nginx/IIS/Cloudflare logs offline, verify bot CIDRs, and discover true orphans and crawl-budget waste. |

### 6 · Automation & AI agents

Let local AI agents drive audits for you.

| Guide | What you'll learn |
|---|---|
| [Local MCP server setup](06-automation-and-ai-agents/local-mcp-server-setup.md) | Enable the optional local Model Context Protocol server over stdio or loopback SSE, with locally generated tokens. |
| [Agent tools & workflows](06-automation-and-ai-agents/agent-tools-and-workflows.md) | Steer CrawlForge with tools like `start-audit`, resources like `audit-summary`, and structured prompts — plus end-to-end agent workflows. |

### 7 · Diagnostics & troubleshooting

Keep CrawlForge healthy and investigate problems locally.

| Guide | What you'll learn |
|---|---|
| [Memory, telemetry & Compact](07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md) | Read the live status bar, memory profiles, and GC stats; use the Compact / Trim Working Set action. |
| [Logs, minidumps & metrics](07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md) | Inspect local structured logs, generate/purge native crash minidumps, and scrape pull-only local Prometheus metrics. |

---

## Suggested learning paths

**New to CrawlForge?** Follow the numbers:
1. [Installation & first launch](01-getting-started/installation-and-first-launch.md) → 2. [Creating projects & scopes](02-managing-projects-and-crawls/creating-projects-and-scopes.md) → 3. [Crawl limits & AIMD politeness](03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md) → 4. [Dashboards & virtualized explorers](04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md).

**Technical SEO consultant** — core audit mastery, then depth:
- [Content scoping](05-advanced-seo-and-intelligence-modules/content-scoping-and-boilerplate-stripping.md) → [N-grams](05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) → [Link graphs & treemaps](04-exploring-reports-and-visualizations/link-graphs-and-treemaps.md) → [Semantic internal linking](05-advanced-seo-and-intelligence-modules/semantic-internal-linking.md) → [Offline server log analyzer](05-advanced-seo-and-intelligence-modules/offline-server-log-analyzer.md).

**Agency digital marketer** — reporting, migrations, and modern surfaces:
- [List Mode & migration matrix](02-managing-projects-and-crawls/list-mode-and-migration-matrix.md) → [Exports & secure destinations](04-exploring-reports-and-visualizations/exports-and-secure-destinations.md) → [Scheduled audits](03-running-and-scheduling-audits/scheduled-audits.md) → [MarTech & Consent Mode](05-advanced-seo-and-intelligence-modules/martech-and-consent-mode-auditing.md) → [AI (GEO) readiness](05-advanced-seo-and-intelligence-modules/ai-geo-readiness.md).

**Solo web developer** — automation and diagnostics:
- [Database configuration](01-getting-started/database-configuration.md) → [Proxies & authenticated crawls](02-managing-projects-and-crawls/proxies-and-authenticated-crawls.md) → [Local MCP server setup](06-automation-and-ai-agents/local-mcp-server-setup.md) → [Agent tools & workflows](06-automation-and-ai-agents/agent-tools-and-workflows.md) → [Logs, minidumps & metrics](07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md).

**"I'm migrating a site to a new domain"** — go straight to [List Mode & migration matrix](02-managing-projects-and-crawls/list-mode-and-migration-matrix.md), then [Lifecycle & checkpointing](03-running-and-scheduling-audits/lifecycle-and-checkpointing.md) for long runs.

**"My crawl keeps getting blocked"** — read [WAF & soft-block detection](05-advanced-seo-and-intelligence-modules/waf-and-soft-block-detection.md) and [Crawl limits & AIMD politeness](03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md).

---

## What CrawlForge deliberately does not do

These are product guarantees, not missing features:

- **No machine fingerprinting** — no hardware IDs, no OS-level machine identity is read, stored, or transmitted.
- **No licensing or entitlement machinery** — no activation, expiry, grace periods, or limits that vary by account state.
- **No accounts or sign-in** — no registration, profiles, or password resets.
- **No remote control plane** — no cloud relay, heartbeat reporting, or remote credential issuance. The *local* MCP server is explicitly permitted and loopback-only.
- **No telemetry** — diagnostics are local-only, user-viewable, and purgeable.
- **No challenge solving or WAF bypass** — detection and reporting only; no CAPTCHA solving, token forging, or evasion automation.
- **No cloud AI services** — embeddings come only from the bundled offline ONNX model or your own loopback daemon; no hosted LLM or embedding APIs.
- **No plugin marketplace** — plugins are local, user-authored artifacts; no remote code loading, and plugins get read-only data (never crawl-control mutations).
- **No remote enrichment** — bot CIDR ranges, agent profiles, WAF signatures, and stop-word lists are bundled assets; no remote reputation or bot-list services. The only optional network action in log analysis is user-consented DNS verification, off by default.

---

## About this documentation

These guides describe the complete extended product defined by the finalized architecture specifications (**FS-CRAWL-001 v1.4** core baseline + **FS-CRAWL-002 v1.1** extensions). Every feature, field name, endpoint, default, and bound documented here is grounded in those specifications. Where a capability is reserved for a future product decision (for example, unattended headless execution or third-party data enrichment), it is **not** documented as an available feature.

*Documentation suite version 1.0 — generated against the finalized architecture set.*