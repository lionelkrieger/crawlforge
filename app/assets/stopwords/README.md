# Bundled Stop-Word Lists (FS-CRAWL-002 §5.2)

N-gram stop-word filtering is **fully offline**: per-language lists ship with
the product as locale assets and are never fetched at runtime. User stop-word
additions are local runtime data (`user_stopwords` table, per language) applied
alongside the bundled list; they are not bundled here.

## Bundled assets

| File | Language | Entries | Provenance |
|---|---|---|---|
| `en.txt` | English | 1,298 | stopwords-iso collection (`stopwords-iso/stopwords-en`) |
| `es.txt` | Spanish | 732 | stopwords-iso collection (`stopwords-iso/stopwords-es`) |
| `fr.txt` | French | 691 | stopwords-iso collection (`stopwords-iso/stopwords-fr`) |
| `de.txt` | German | 620 | stopwords-iso collection (`stopwords-iso/stopwords-de`) |
| `LICENSE.txt` | — | — | MIT (Gene Diaz, 2016), verbatim upstream license |

Acquired at **build time** (2026-11-14) from
`https://raw.githubusercontent.com/stopwords-iso/stopwords-<lang>/master/stopwords-<lang>.txt`.
The stopwords-iso collection aggregates widely used public stop-word lists
(Ranks NL, MySQL fulltext, NLTK-derived and similar public sources) under the
MIT license reproduced in `LICENSE.txt`.

## Format

One lowercase stop word per line, UTF-8, no header. The filter case-folds
tokens before lookup, so list casing is not significant. Lines starting with
`#` and blank lines are ignored by the loader.

## Language resolution

Same selection rule as spelling dictionaries (§7.8): the page's declared
language selects the list (`en-GB` → `en`), falling back to the user's
preferred analysis language. Unknown languages with no fallback list apply
no bundled filtering (user additions still apply).

## Adding locales

Drop additional `<lang>.txt` lists (open-licensed only) into this folder and
document provenance in this table.
