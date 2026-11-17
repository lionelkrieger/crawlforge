# Bundled SERP Font-Metric Tables (FS-CRAWL-002 §15.3)

Pixel-width SERP/social measurement is **fully offline**: per-character
advance-width tables ship with the product and are never fetched at runtime.
Width = Σ(glyph advance widths) × fontSize / unitsPerEm, per §15.3 ("widths
computed from bundled per-locale font-metric tables"). Kerning is intentionally
not applied — the spec defines the sum-of-advances model.

## Profiles

| Profile key | Family | Size | Role |
|---|---|---|---|
| `arial-desktop-title` | Arial (Helvetica-metric-compatible) | 20px | Desktop SERP title |
| `arial-desktop-description` | Arial (Helvetica-metric-compatible) | 14px | Desktop SERP description |
| `roboto-mobile-title` | Roboto | 18px | Mobile SERP title |
| `roboto-mobile-description` | Roboto | 14px | Mobile SERP description |

Font sizes are documented approximations of Google SERP rendering. Default
pixel budgets (§15.3): desktop title ≈580px; mobile title ≈920px (2 lines);
desktop description ≈920px (2 lines); mobile description ≈920px. Budgets are
configuration, not baked into the tables.

## Provenance

| Source file | Origin | License |
|---|---|---|
| `NimbusSans-Regular.afm` | URW base35 fonts (`ArtifexSoftware/urw-base35-fonts`, `fonts/NimbusSans-Regular.afm`) | GPL + font exception (`URW-LICENSE.txt`, `URW-COPYING.txt`) |
| `Roboto-Regular.ttf` | Google Fonts (`google/fonts`, `ofl/roboto/Roboto[wdth,wght].ttf`), default instance = Regular 400 / width 100 | SIL OFL 1.1 (`Roboto-OFL.txt`) |

Nimbus Sans is URW's metrically compatible clone of Helvetica; Arial is
metrically compatible with Helvetica, so its advance widths are the standard
Arial core metrics (verified: space=278, A=667, W=944, i=222, ellipsis=1000
per 1000 units/em). Roboto widths were extracted from the TTF `hmtx`/`cmap`
tables (unitsPerEm=2048, normalized to 1000).

Acquired at **build time** (2026-11-14). The generator
`generate-tables.ps1` (provenance tool, not used at runtime) parses the AFM
glyph-name → Unicode mapping (AFM codes >127 are MacRoman positions, NOT
Unicode) and the TTF cmap format 4 / hmtx advances, then emits the four
profile JSON files. Re-run with `pwsh -File generate-tables.ps1`.

## JSON shape

```json
{
  "profileKey": "arial-desktop-title",
  "fontFamily": "Arial (Helvetica-metric-compatible)",
  "fontSizePx": 20,
  "unitsPerEm": 1000,
  "defaultWidth": 500,
  "widths": { "32": 278, "65": 667, "...": 0 },
  "provenance": "..."
}
```

`widths` keys are decimal Unicode codepoints; values are advances per 1000
units/em. Coverage: printable ASCII, Latin-1 supplement, and common SERP
typography (en/em dash, curly quotes, ellipsis, euro, trademark). Codepoints
outside the table fall back to `defaultWidth` (documented approximation).

## Limits

- Sum-of-advances model only (no kerning, no ligature substitution, no
  hinting) — matches the §15.3 measurement definition.
- Truncation model: longest prefix whose width plus the ellipsis width fits
  the budget, then the ellipsis is appended (Google-style `…` truncation).
- Non-Latin scripts fall back to `defaultWidth` per codepoint; per-locale
  tables can be added additively with documented provenance.
