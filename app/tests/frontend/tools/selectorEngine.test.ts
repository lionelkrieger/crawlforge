// @vitest-environment jsdom
/**
 * Resilient-selector capture engine tests (criterion #42): finder produces
 * a unique CSS selector constrained to the sandbox root, paired with a
 * positional XPath alternative; match counts preview what the crawl sees.
 */

import { describe, expect, it } from 'vitest';
import { captureSelector, countCssMatches, textPreviewOf } from '../../../src/frontend/src/tools/picker/selectorEngine';
import { buildXPath, countXPathMatches } from '../../../src/frontend/src/tools/picker/xpath';
import { ARTIFACT_HTML } from './helpers';

function artifactRoot(): { doc: Document; root: Element } {
  const doc = new DOMParser().parseFromString(ARTIFACT_HTML, 'text/html');
  return { doc, root: doc.body };
}

describe('captureSelector', () => {
  it('captures a unique CSS selector for the article title', () => {
    const { root } = artifactRoot();
    const h2 = root.querySelector('h2.post-title')!;
    const capture = captureSelector(h2, root);
    expect(capture.css.length).toBeGreaterThan(0);
    expect(capture.matchCount).toBe(1);
    expect(root.querySelectorAll(capture.css).length).toBe(1);
    expect(root.querySelector(capture.css)).toBe(h2);
    expect(capture.tag).toBe('h2');
    expect(capture.textPreview).toBe('Local-first crawling');
  });

  it('pairs a positional XPath that resolves to the same element', () => {
    const { root } = artifactRoot();
    const lead = root.querySelector('p.lead')!;
    const capture = captureSelector(lead, root);
    expect(capture.xpath.startsWith('./')).toBe(true);
    expect(capture.xpathMatchCount).toBe(1);
    expect(countXPathMatches(root, capture.xpath).count).toBe(1);
  });

  it('produces unique selectors across distinct elements', () => {
    const { root } = artifactRoot();
    const targets = ['nav.site-nav a', 'p.lead', 'aside.sidebar', 'footer', 'article.post strong'];
    const selectors = targets.map((css) => captureSelector(root.querySelector(css)!, root).css);
    expect(new Set(selectors).size).toBe(targets.length);
    for (const selector of selectors) {
      expect(countCssMatches(root, selector).count).toBe(1);
    }
  });
});

describe('countCssMatches', () => {
  it('counts matches under the root', () => {
    const { root } = artifactRoot();
    expect(countCssMatches(root, 'p').count).toBe(2);
    expect(countCssMatches(root, 'main article').count).toBe(1);
    expect(countCssMatches(root, '.does-not-exist').count).toBe(0);
  });

  it('surfaces syntax errors instead of throwing', () => {
    const { root } = artifactRoot();
    const result = countCssMatches(root, 'p[');
    expect(result.count).toBe(0);
    expect(result.error).not.toBeNull();
  });
});

describe('buildXPath / countXPathMatches', () => {
  it('encodes structure with 1-based sibling positions', () => {
    const { root } = artifactRoot();
    const secondParagraph = root.querySelectorAll('article p')[1];
    expect(buildXPath(secondParagraph, root)).toBe('./main[1]/article[1]/p[2]');
    expect(countXPathMatches(root, './main[1]/article[1]/p[2]').count).toBe(1);
  });

  it('returns the context node for the root itself', () => {
    const { root } = artifactRoot();
    expect(buildXPath(root, root)).toBe('.');
  });

  it('reports invalid XPath as an error, not a throw', () => {
    const { root } = artifactRoot();
    const result = countXPathMatches(root, './[[[');
    expect(result.count).toBe(0);
    expect(result.error).not.toBeNull();
  });
});

describe('textPreviewOf', () => {
  it('collapses whitespace and caps at 80 characters with an ellipsis', () => {
    const { root } = artifactRoot();
    const article = root.querySelector('article.post')!;
    const preview = textPreviewOf(article);
    expect(preview.length).toBeLessThanOrEqual(80);
    expect(preview.endsWith('…')).toBe(true);
    expect(preview).not.toContain('\n');
  });

  it('keeps short text intact', () => {
    const { root } = artifactRoot();
    expect(textPreviewOf(root.querySelector('h2')!)).toBe('Local-first crawling');
  });
});