/**
 * Offline artifact sanitizer for the sandboxed preview (FS-CRAWL-001 §12,
 * FS-CRAWL-002 §8.9). The preview loads ONLY the crawled target's stored
 * rendered artifact — never the live site, never its subresources.
 *
 * Guarantees after sanitization:
 *  - no script execution (script nodes and on* handlers removed);
 *  - no external loads (remote img/srcset/source/link/iframe/object/embed
 *    stripped; only data: URLs survive on media);
 *  - no navigation (anchors defanged, forms neutralized, meta refresh gone);
 *  - inline styles and structure preserved so selectors stay meaningful.
 *
 * Pure string → string via DOMParser; deterministic and unit-testable.
 *
 * @module tools/picker/sanitize
 */

/** Elements removed wholesale (active content / external frames). */
const REMOVED_TAGS = new Set(['script', 'iframe', 'object', 'embed', 'noscript', 'base', 'link', 'meta']);

/** Media elements whose external sources are stripped. */
const MEDIA_TAGS = new Set(['img', 'source', 'video', 'audio', 'track', 'input']);

/** Attributes that can trigger external loads or navigation. */
const URL_ATTRIBUTES = ['src', 'srcset', 'href', 'action', 'formaction', 'poster', 'data', 'xlink:href'];

/** True when the value is an inline data: URL (the only allowed media source). */
export function isDataUrl(value: string): boolean {
  return value.trim().toLowerCase().startsWith('data:');
}

/**
 * Sanitizes a rendered HTML artifact for offline sandboxed display.
 * Accepts a full document or a fragment; returns a fragment string.
 */
export function sanitizeArtifact(html: string): string {
  const parser = new DOMParser();
  const doc = parser.parseFromString(html, 'text/html');

  // Walk a static snapshot — removals mutate the live collections.
  const all = Array.from(doc.body.querySelectorAll('*'));
  for (const element of all) {
    const tag = element.tagName.toLowerCase();

    if (REMOVED_TAGS.has(tag)) {
      element.remove();
      continue;
    }

    // Strip inline event handlers (onclick, onload, …).
    for (const attribute of Array.from(element.attributes)) {
      const name = attribute.name.toLowerCase();
      if (name.startsWith('on')) {
        element.removeAttribute(attribute.name);
        continue;
      }
      if (URL_ATTRIBUTES.includes(name)) {
        const value = attribute.value;
        const keepMedia = (tag === 'img' || tag === 'source' || tag === 'video' || tag === 'audio') && name === 'src';
        if (keepMedia && isDataUrl(value)) continue;
        if (tag === 'a' && name === 'href') {
          // Defang navigation but keep the text readable.
          element.setAttribute('href', '#');
          element.setAttribute('data-cf-href', value);
          continue;
        }
        element.removeAttribute(attribute.name);
      }
    }

    // Inline style: drop url(...) loads, keep the rest.
    const style = element.getAttribute('style');
    if (style && /url\s*\(/i.test(style)) {
      element.setAttribute('style', style.replace(/url\s*\([^)]*\)/gi, 'none'));
    }

    if (MEDIA_TAGS.has(tag)) {
      element.removeAttribute('srcset');
    }
    if (tag === 'form') {
      element.setAttribute('action', '#');
    }
  }

  // Style elements: keep inline CSS but neutralize @import / external urls.
  for (const styleElement of Array.from(doc.querySelectorAll('style'))) {
    styleElement.textContent = (styleElement.textContent ?? '')
      .replace(/@import[^;]+;?/gi, '')
      .replace(/url\s*\(([^)]*)\)/gi, (match, inner: string) =>
        isDataUrl(inner.replace(/['"]/g, '')) ? match : 'none'
      );
  }

  return doc.body.innerHTML;
}