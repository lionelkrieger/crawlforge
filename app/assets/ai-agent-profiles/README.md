# AI agent profiles — bundled catalogue (WP-04-03)

`ai-agent-profiles.json` is the bundled, versioned AI-agent profile catalogue
for the bot-governance advisory matrix (FS-CRAWL-002 §13.2, criterion #46).
It follows the §7.15 bundled-asset pattern established by the technology
fingerprint catalogue.

## Provenance

**In-house curated knowledge.** Every profile was authored in-house from
publicly documented vendor crawler documentation (Google Search Central,
OpenAI crawler docs, Anthropic crawler docs, Microsoft Bing webmaster docs,
Perplexity, Common Crawl, Apple, Amazon, ByteDance, Cohere, DuckDuckGo,
You.com public statements). **No third-party dataset, remote registry, or
vendor service is copied, fetched, or consulted** — at build time or at
runtime (exclusions E2/E9). The catalogue ships with the product and is
updated only through product updates.

## Offline posture

- Runtime consumption is `File.ReadAllText` on this local asset only.
- **Zero runtime fetch. Zero remote refresh.** The governance matrix is an
  ADVISORY report: it never modifies `robots.txt`, never alters crawl
  behavior, and never submits anything to any vendor.

## Schema

| Field | Meaning |
|---|---|
| `version` / `revision` | Catalogue version snapshot (audits may record it). |
| `profiles[].agentToken` | Canonical robots.txt token / agent key (unique). |
| `profiles[].userAgentPatterns` | Case-insensitive substring tokens matched against observed User-Agent strings. |
| `profiles[].purposeClass` | `search-index` \| `ai-rag-citation` \| `llm-training` (maps to `AiAgentPurpose` Search/Rag/Training). |
| `profiles[].vendor` | Operating vendor label. |
| `profiles[].policyNotes` | Human-readable policy context for the advisory surface. |

## Coverage

The nine agents mandated by §13.2 (Googlebot, Bingbot, OAI-SearchBot,
PerplexityBot, GPTBot, ClaudeBot, Google-Extended, CCBot, Applebot-Extended)
plus five widely documented agents (Amazonbot, Bytespider, cohere-ai,
DuckAssistBot, YouBot). Purpose classes follow the §13.2 table; additions are
classified from vendor documentation.

Notable subtleties documented per profile:
- `Google-Extended` is a robots-only token (never a fetching User-Agent).
- `OAI-SearchBot` (citation) is independent of `GPTBot` (training).
- `Applebot-Extended` (training) is independent of `Applebot` (search/Siri).

## Maintenance

Additive only: new profiles may be appended; existing `agentToken` values are
never repurposed. Bump `version`/`revision` on any change. Loader validation
rejects duplicate tokens, unknown purpose classes, and empty pattern lists.
