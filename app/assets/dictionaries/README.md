# Bundled Spelling Dictionaries (FS-CRAWL-001 §7.8)

Runtime spelling checks are **fully offline**: dictionaries ship with the
product and are never fetched at runtime. The checker selects the dictionary
from the page's declared language, falling back to the user's preferred
checking language. Per-language **user dictionaries** are local runtime data
(not bundled here); their entries suppress findings.

## Bundled assets

| File | Provenance | License |
|---|---|---|
| `en_US.dic` | LibreOffice `dictionaries` repository, `en/en_US.dic` (SCOWL-derived en_US word list, 49,568 entries incl. affix flags) | BSD-style (see `license.txt`) |
| `en_US.aff` | LibreOffice `dictionaries` repository, `en/en_US.aff` (UTF-8, apostrophe WORDCHARS, ICONV curly-quote folding) | BSD-style (see `license.txt`) |
| `license.txt` | Verbatim license from the same upstream directory | — |

Acquired at **build time** (2026-11-14) from:
`https://raw.githubusercontent.com/LibreOffice/dictionaries/master/en/`

The word list derives from SCOWL (Spell Checking Oriented Word Lists) by Kevin
Atkinson, distributed under the BSD-style license reproduced in `license.txt`.
Hunspell affix compression means the effective vocabulary is larger than the
raw entry count.

## Format

Standard Hunspell `.dic`/`.aff` pair, read by the pure-managed
`WeCantSpell.Hunspell` reader (no native dependencies). First line of the
`.dic` is the entry count; entries may carry `/flags` referencing affix rules.

## Adding locales

Drop additional `<lang>_<REGION>.dic` + `.aff` pairs (open-licensed only) into
this folder and document provenance in this table. Language resolution maps
`en`, `en-US`, `en-GB` → `en_US` (nearest available); unknown languages fall
back to the configured preferred language.

## Limits

- Only `en_US` is bundled in this wave; other locales resolve to the fallback
  dictionary or skip checking when no dictionary is available (documented
  behavior, never a runtime fetch).
- Proper-noun and code-like token exclusion is handled by generic heuristics
  in the checker (implementation choice per §7.8), not by the dictionary.
