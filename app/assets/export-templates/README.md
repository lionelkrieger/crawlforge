# Export Templates

Self-contained HTML report chrome for `CrawlForge.Export` (FS-CRAWL-001 §8.4).

## Files

- `report-template.html` — optional override of the engine's built-in offline
  template. The engine resolves it from this asset directory when a template
  directory is supplied; the built-in template is the fallback, so exports
  remain fully self-contained even when assets are not deployed.

## Contract

- The template MUST contain the `{{CONTENT}}` placeholder; the engine replaces
  it with the generated report (header + one table per dataset).
- The template MUST be self-contained: inline CSS only. No external scripts,
  stylesheets, fonts, images, or network references — the report renders
  offline with zero outbound traffic (criterion #7, network boundary §2.6).
- All dataset cell values are HTML-escaped by the writer before insertion;
  template authors must not inject raw data placeholders other than
  `{{CONTENT}}`.

## Provenance

Original CrawlForge authoring (this workpiece, WP-05-03). No third-party
template content.
