/**
 * Visual selector picker (criterion #42, FS-CRAWL-002 §7.9/§4.2).
 *
 * Click a rendered element in the sandboxed preview → resilient CSS
 * selector (@medv/finder) + positional XPath alternative + live match-count
 * preview. Captured selectors send straight into Custom Extraction rule
 * drafts and Content Area Scoping include/exclude lists.
 *
 * The sandbox loads ONLY the crawled target's stored rendered artifact,
 * sanitized for offline display (no scripts, no external subresources).
 * Two render modes: `iframe` (production sandbox, allow-same-origin only)
 * and `inline` (sanitized DOM in-process — the jsdom-testable path and the
 * graceful fallback when iframe loading is unavailable).
 *
 * @module tools/picker/SelectorPicker
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Badge, Box, Button, Group, Stack, Text, TextInput } from '@mantine/core';
import { IconCheck, IconExport, IconPlus, IconSearch, IconX } from '../../theme/icons';
import { CodeBlock } from '../shared/CodeBlock';
import { sanitizeArtifact } from './sanitize';
import { captureSelector, countCssMatches, textPreviewOf, type CssCount, type SelectorCapture } from './selectorEngine';
import './picker.css';

/**
 * Production sandbox attribute: same-origin ONLY — no scripts, no forms,
 * no popups, no top-navigation. The artifact is inert display content;
 * interaction is delegated from the parent document.
 */
export const PICKER_SANDBOX = 'allow-same-origin';

/**
 * Builds the iframe srcdoc: the sanitized stored artifact, nothing else.
 * The sandbox never loads a remote URL — only the crawled target's
 * rendered HTML, stripped of scripts and external subresources.
 */
export function buildSandboxSrcDoc(html: string): string {
  return sanitizeArtifact(html);
}

/** Highlight styles injected into the iframe sandbox document. */
const SANDBOX_HIGHLIGHT_CSS = `
[data-cf-picker-hover] { outline: 2px dashed rgba(34, 184, 207, 0.9) !important; outline-offset: 1px; cursor: crosshair !important; }
[data-cf-picker-selected] { outline: 3px solid rgba(255, 138, 61, 0.95) !important; outline-offset: 1px; }
[data-cf-picker-match] { background: rgba(255, 138, 61, 0.16) !important; }
body { cursor: crosshair; }
`;

/** SelectorPicker props. */
export interface SelectorPickerProps {
  /** Stored rendered artifact HTML (sanitized before display). */
  readonly html: string;
  /** Render mode: iframe sandbox (default) or inline sanitized DOM. */
  readonly mode?: 'iframe' | 'inline';
  /** Send the captured CSS selector to a Custom Extraction rule draft. */
  readonly onSendToExtraction?: (selector: string) => void;
  /** Send the captured CSS selector to a Content Scope list. */
  readonly onSendToScope?: (selector: string, list: 'include' | 'exclude') => void;
  /** Observe every capture (embedding surfaces). */
  readonly onCapture?: (capture: SelectorCapture) => void;
  /** Test identifier root. */
  readonly testId?: string;
}

/** The visual selector picker. */
export function SelectorPicker({
  html,
  mode = 'iframe',
  onSendToExtraction,
  onSendToScope,
  onCapture,
  testId = 'selector-picker'
}: SelectorPickerProps) {
  const sanitized = useMemo(() => buildSandboxSrcDoc(html), [html]);
  const inlineRootRef = useRef<HTMLDivElement | null>(null);
  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  const [sandboxRoot, setSandboxRoot] = useState<Element | null>(null);
  const [capture, setCapture] = useState<SelectorCapture | null>(null);
  const [manualCss, setManualCss] = useState('');
  const [manualCount, setManualCount] = useState<CssCount>({ count: 0, error: null });
  const [sent, setSent] = useState<string | null>(null);

  // Resolve the interaction root: inline container or iframe body.
  useEffect(() => {
    if (mode === 'inline') {
      setSandboxRoot(inlineRootRef.current);
    }
  }, [mode, sanitized]);

  const handleIframeLoad = useCallback(() => {
    const doc = iframeRef.current?.contentDocument;
    if (!doc?.body) return;
    const style = doc.createElement('style');
    style.textContent = SANDBOX_HIGHLIGHT_CSS;
    doc.head.appendChild(style);
    setSandboxRoot(doc.body);
  }, []);

  // Clear previous highlights, then mark the selection and its matches.
  const highlight = useCallback((root: Element, css: string | null) => {
    for (const node of Array.from(root.querySelectorAll('[data-cf-picker-selected],[data-cf-picker-match]'))) {
      node.removeAttribute('data-cf-picker-selected');
      node.removeAttribute('data-cf-picker-match');
    }
    if (!css) return;
    try {
      const matches = Array.from(root.querySelectorAll(css));
      for (const node of matches) node.setAttribute('data-cf-picker-match', 'true');
      matches[0]?.setAttribute('data-cf-picker-selected', 'true');
    } catch {
      // Invalid manual selector — nothing to highlight.
    }
  }, []);

  // Delegated pointer interaction on the sandbox root (works across the
  // iframe boundary, where React synthetic events do not reach).
  useEffect(() => {
    const root = sandboxRoot;
    if (!root) return;

    const elementOf = (event: Event): Element | null => {
      const target = event.target;
      return target instanceof Element && root.contains(target) ? target : null;
    };

    const onClick = (event: Event) => {
      const element = elementOf(event);
      if (!element) return;
      event.preventDefault();
      event.stopPropagation();
      const next = captureSelector(element, root);
      setCapture(next);
      setManualCss(next.css);
      setManualCount({ count: next.matchCount, error: null });
      highlight(root, next.css);
      onCapture?.(next);
    };

    const onOver = (event: Event) => {
      const element = elementOf(event);
      if (element) element.setAttribute('data-cf-picker-hover', 'true');
    };
    const onOut = (event: Event) => {
      const element = elementOf(event);
      if (element) element.removeAttribute('data-cf-picker-hover');
    };

    root.addEventListener('click', onClick, true);
    root.addEventListener('mouseover', onOver);
    root.addEventListener('mouseout', onOut);
    return () => {
      root.removeEventListener('click', onClick, true);
      root.removeEventListener('mouseover', onOver);
      root.removeEventListener('mouseout', onOut);
    };
  }, [sandboxRoot, highlight, onCapture]);

  // Live manual-selector match preview.
  useEffect(() => {
    if (!sandboxRoot) return;
    if (manualCss.trim().length === 0) {
      setManualCount({ count: 0, error: null });
      return;
    }
    setManualCount(countCssMatches(sandboxRoot, manualCss));
  }, [manualCss, sandboxRoot]);

  const applyManual = useCallback(() => {
    if (!sandboxRoot || manualCss.trim().length === 0) return;
    const count = countCssMatches(sandboxRoot, manualCss);
    if (count.error) return;
    const first = sandboxRoot.querySelector(manualCss);
    const next: SelectorCapture = first
      ? { ...captureSelector(first, sandboxRoot), css: manualCss, matchCount: count.count }
      : {
          css: manualCss,
          xpath: '',
          matchCount: 0,
          xpathMatchCount: 0,
          tag: '(none)',
          textPreview: ''
        };
    setCapture(next);
    highlight(sandboxRoot, manualCss);
    onCapture?.(next);
  }, [sandboxRoot, manualCss, highlight, onCapture]);

  const send = useCallback(
    (target: 'extraction' | 'include' | 'exclude') => {
      if (!capture) return;
      if (target === 'extraction') onSendToExtraction?.(capture.css);
      else onSendToScope?.(capture.css, target);
      setSent(target);
      window.setTimeout(() => setSent(null), 1400);
    },
    [capture, onSendToExtraction, onSendToScope]
  );

  return (
    <Stack gap="sm" data-testid={testId}>
      <Group justify="space-between" wrap="nowrap">
        <Group gap={8}>
          <IconSearch size={15} color="var(--mantine-color-signal-5)" />
          <Text size="sm" fw={600}>
            Click an element to capture its selector
          </Text>
        </Group>
        <Badge size="xs" variant="outline" color="steel" data-testid={`${testId}-mode`}>
          {mode === 'iframe' ? 'Sandboxed iframe' : 'Inline sandbox'} · offline artifact
        </Badge>
      </Group>

      <Box className="cf-picker-viewport" data-testid={`${testId}-viewport`}>
        {mode === 'iframe' ? (
          <iframe
            ref={iframeRef}
            title="Sandboxed rendered artifact"
            sandbox={PICKER_SANDBOX}
            srcDoc={sanitized}
            onLoad={handleIframeLoad}
            className="cf-picker-iframe"
            data-testid={`${testId}-iframe`}
          />
        ) : (
          <Box
            ref={inlineRootRef}
            className="cf-picker-inline cf-picker-surface"
            dangerouslySetInnerHTML={{ __html: sanitized }}
            data-testid={`${testId}-inline`}
          />
        )}
      </Box>

      <Group gap={6} wrap="nowrap" align="flex-end">
        <TextInput
          flex={1}
          size="xs"
          ff="monospace"
          label="Manual selector"
          placeholder="main article h2"
          value={manualCss}
          onChange={(event) => setManualCss(event.currentTarget.value)}
          error={manualCount.error ?? undefined}
          data-testid={`${testId}-manual`}
        />
        <Button size="xs" variant="light" color="signal" onClick={applyManual} data-testid={`${testId}-manual-apply`}>
          Preview
        </Button>
        <Badge
          size="lg"
          variant="light"
          color={manualCount.error ? 'red' : manualCount.count > 0 ? 'signal' : 'steel'}
          data-testid={`${testId}-manual-count`}
        >
          {manualCount.error ? 'invalid' : `${manualCount.count} match${manualCount.count === 1 ? '' : 'es'}`}
        </Badge>
      </Group>

      {capture ? (
        <Box className="cf-picker-capture" data-testid={`${testId}-capture`}>
          <Group justify="space-between" mb={8}>
            <Group gap={8}>
              <Badge size="sm" color="ember" variant="light" data-testid={`${testId}-tag`}>
                &lt;{capture.tag}&gt;
              </Badge>
              <Badge size="sm" color="signal" variant="light" data-testid={`${testId}-match-count`}>
                {capture.matchCount} CSS match{capture.matchCount === 1 ? '' : 'es'}
              </Badge>
              <Badge size="sm" color="steel" variant="outline" data-testid={`${testId}-xpath-count`}>
                {capture.xpathMatchCount} XPath match{capture.xpathMatchCount === 1 ? '' : 'es'}
              </Badge>
            </Group>
            {capture.textPreview ? (
              <Text size="xs" c="dimmed" maw={280} truncate>
                {capture.textPreview}
              </Text>
            ) : null}
          </Group>
          <CodeBlock label="Resilient CSS selector" code={capture.css} testId={`${testId}-css`} noCopy={false} />
          <Box mt={8}>
            <CodeBlock label="XPath alternative" code={capture.xpath || '(no match)'} testId={`${testId}-xpath`} />
          </Box>
          <Group gap={6} mt={10}>
            {onSendToExtraction ? (
              <Button
                size="xs"
                variant="light"
                color="ember"
                leftSection={sent === 'extraction' ? <IconCheck size={13} /> : <IconExport size={13} />}
                onClick={() => send('extraction')}
                data-testid={`${testId}-send-extraction`}
              >
                {sent === 'extraction' ? 'Sent to rule draft' : 'Send to extraction rule'}
              </Button>
            ) : null}
            {onSendToScope ? (
              <>
                <Button
                  size="xs"
                  variant="light"
                  color="green"
                  leftSection={sent === 'include' ? <IconCheck size={13} /> : <IconPlus size={13} />}
                  onClick={() => send('include')}
                  data-testid={`${testId}-send-include`}
                >
                  {sent === 'include' ? 'Added' : 'Scope include'}
                </Button>
                <Button
                  size="xs"
                  variant="light"
                  color="red"
                  leftSection={sent === 'exclude' ? <IconCheck size={13} /> : <IconX size={13} />}
                  onClick={() => send('exclude')}
                  data-testid={`${testId}-send-exclude`}
                >
                  {sent === 'exclude' ? 'Added' : 'Scope exclude'}
                </Button>
              </>
            ) : null}
          </Group>
        </Box>
      ) : (
        <Text size="xs" c="dimmed" fs="italic" data-testid={`${testId}-hint`}>
          Nothing captured yet — click any element above, or type a selector and preview its match count.
        </Text>
      )}
    </Stack>
  );
}

/** Re-export for embedding surfaces that display captured text previews. */
export { textPreviewOf };