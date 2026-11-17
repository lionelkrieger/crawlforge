# Bot CIDR & User-Agent Catalogue

Bundled, versioned bot classification catalogue for the CrawlForge Server Log
File Analyzer (FS-CRAWL-002 §11.3, acceptance criterion #44). Consumed by
`CrawlForge.Intelligence.LogAnalyzer.BotCatalogue` / `BotVerifier`.

## Offline posture (mandatory)

- This asset is **bundled with the product** and loaded exclusively via local
  file read. There is **no runtime fetch path**, no remote reputation service,
  and no auto-update mechanism (clean-room exclusion E8).
- The catalogue updates **only with product updates** (version/revision fields
  below are bumped by the maintainer, never at runtime).
- User-local CIDR additions persist in `application_settings` (JSON) and are
  merged at load time by the consumer; they never mutate this bundled file.
- DNS verification is the single optional network action: user-triggered, off
  by default, per-import consent, bounded sample, locally logged (§11.3).
  Import-time classification is **100% offline**.

## Provenance

- **Authorship:** curated in-house for CrawlForge (WP-04-05). No third-party
  dataset file was copied or imported. Entries were transcribed from publicly
  documented, observable vendor publications:
  - **User-agent patterns:** the vendor-documented UA tokens each crawler
    announces (Googlebot/2.1 with and without `Mobile`, bingbot/2.0, Applebot,
    OAI-SearchBot/1.0, GPTBot/1.0, ClaudeBot/1.0, PerplexityBot/1.0) plus
    widely observed generic bot tokens for the `other-bot` bucket.
  - **CIDR ranges:** representative published crawler ranges as documented by
    the vendors in their public crawler-IP publications (Google's googlebot
    IP-range publication, Microsoft's bingbot IP-range publication, Apple's
    documented `17.0.0.0/8` Applebot range, OpenAI's gptbot/searchbot IP-range
    publications, Anthropic's claudebot IP-range publication). The set is
    **representative, not exhaustive**: vendors rotate ranges, and coverage
    gaps are expected and safe — a UA-matched hit without a CIDR match stays
    `claimed` (unverified) and is **labeled, never silently trusted**.
  - **DNS suffixes:** the vendor-documented reverse-DNS zones used for
    forward-confirmed rDNS (`.googlebot.com`/`.google.com`, `.search.msn.com`,
    `.applebot.apple.com`, `.openai.com`, `.anthropic.com`, `.perplexity.ai`).
- **License:** original work product of the CrawlForge build; no external
  dataset license applies. Bot and vendor names are nominative references to
  the identified crawlers, not endorsements.
- **Maintenance duty:** on each product update, re-verify the CIDR sets against
  the vendors' current publications and bump `revision`. Families with empty
  `cidrs` (e.g. PerplexityBot at r1) intentionally classify as `claimed` only
  until a maintainer adds verified ranges.

## Versioning

| Field | Value | Meaning |
|---|---|---|
| `version` | `2026.1` | Product-update cadence identifier. |
| `revision` | `r1` | Revision within the version; bumped on any entry change. |

## Schema

```jsonc
{
  "version": "2026.1",
  "revision": "r1",
  "families": [
    {
      "key": "googlebot-desktop",      // stable family key (persisted in breakdowns)
      "name": "Googlebot Desktop",     // display label (BotClassification.Family)
      "purpose": "search",             // search | ai-search | ai-training (§11.3/§13 vocabulary)
      "matchOrder": 20,                // ascending evaluation order; first match wins
      "userAgentPatterns": [ "Googlebot" ],  // case-insensitive .NET regex, 100ms timeout
      "dnsSuffixes": [ ".googlebot.com" ],   // forward-confirmed rDNS zones
      "cidrs": [ "66.249.64.0/19" ]    // IPv4/IPv6 CIDR ranges, offline match
    }
  ]
}
```

## Classification rules

1. Missing/empty user-agent → `unknown` bucket (never a bot).
2. Families are evaluated in `matchOrder` ascending; the first regex match wins
   (Googlebot Smartphone precedes Desktop so mobile hits are not absorbed).
3. No family match but a generic bot token present → `other-bot`.
4. No bot evidence → `human`.
5. Verification tiers (§11.3): `claimed` (UA match only), `cidr-verified`
   (offline CIDR match), `dns-verified` (user-triggered forward-confirmed rDNS
   only). A CIDR match without a UA match never classifies a hit as a bot on
   its own — both signals are reported, and unverified bots remain labeled.
6. `search-bot hits` (correlation, §11.5) counts families with purpose `search`
   only; `ai-search` and `ai-training` families are reported separately.

## Maintenance

1. Add/adjust entries in `bot-catalogue.json`; keep keys stable (never
   repurpose an existing key — persisted breakdowns reference it).
2. Bump `revision` (and `version` on product updates).
3. Run the WP-04-05 suite:
   `dotnet test app/tests/CrawlForge.Intelligence.Tests --filter FullyQualifiedName~LogAnalyzer`.
