/**
 * XPath alternative for captured selectors (criterion #42). Builds a
 * positional XPath from the capture root to the element — resilient to
 * class/id churn because it encodes structure, not names — and counts
 * matches through the document's XPath evaluator.
 *
 * @module tools/picker/xpath
 */

/** Position of an element among same-tag siblings (1-based XPath index). */
function siblingIndex(element: Element): number {
  const parent = element.parentElement;
  if (!parent) return 1;
  const sameTag = Array.from(parent.children).filter((child) => child.tagName === element.tagName);
  return sameTag.indexOf(element) + 1;
}

/**
 * Builds an absolute-positional XPath from `root` to `element`.
 * The root itself is the context node (`.`), so the path is relative to
 * the sandbox body and evaluates with root as context.
 */
export function buildXPath(element: Element, root: Element): string {
  const segments: string[] = [];
  let current: Element | null = element;
  while (current && current !== root) {
    const tag = current.tagName.toLowerCase();
    segments.unshift(`${tag}[${siblingIndex(current)}]`);
    current = current.parentElement;
  }
  if (current !== root) {
    // Element is outside the root — fall back to a document-absolute path.
    return buildDocumentXPath(element);
  }
  return segments.length > 0 ? `./${segments.join('/')}` : '.';
}

/** Document-absolute positional XPath (fallback when outside the root). */
function buildDocumentXPath(element: Element): string {
  const segments: string[] = [];
  let current: Element | null = element;
  while (current && current.parentElement) {
    segments.unshift(`${current.tagName.toLowerCase()}[${siblingIndex(current)}]`);
    current = current.parentElement;
  }
  return `/${segments.join('/')}`;
}

/** Result of evaluating an XPath against the sandbox root. */
export interface XPathCount {
  readonly count: number;
  readonly error: string | null;
}

/** Counts nodes matching an XPath with `root` as the context node. */
export function countXPathMatches(root: Element, xpath: string): XPathCount {
  try {
    const doc = root.ownerDocument;
    const result = doc.evaluate(xpath, root, null, XPathResult.ORDERED_NODE_ITERATOR_TYPE, null);
    let count = 0;
    while (result.iterateNext()) count += 1;
    return { count, error: null };
  } catch (error) {
    return { count: 0, error: error instanceof Error ? error.message : 'Invalid XPath expression.' };
  }
}