// @vitest-environment jsdom
/**
 * Tools workspace + shell mount tests: the catalog rail navigates every
 * instrument, the initial tool is honored, and the granted ToolsView frame
 * mounts the workspace end to end.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { TOOL_CATALOG_KEYS, ToolsWorkspace } from '../../../src/frontend/src/tools/ToolsWorkspace';
import { ToolsView } from '../../../src/frontend/src/shell/ToolsView';
import { byTestId, click, flush, renderTool, toolServices, waitFor } from './helpers';
import type { Mounted } from '../shell/helpers';

let mounted: Mounted | null = null;

beforeEach(() => {
  localStorage.clear();
  document.body.replaceChildren();
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

describe('ToolsWorkspace', () => {
  it('exposes the full twelve-instrument catalog', () => {
    expect(TOOL_CATALOG_KEYS).toEqual([
      'single-page',
      'serp',
      'render',
      'robots',
      'sitemap',
      'scope',
      'rewrite',
      'llms',
      'urlcheck',
      'structured',
      'query',
      'destinations'
    ]);
  });

  it('opens on the Single-Page Analyzer by default', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<ToolsWorkspace />, services);
    expect(byTestId(mounted.container, 'tools-workspace')).not.toBeNull();
    expect(byTestId(mounted.container, 'tools-rail')!.querySelectorAll('.cf-tools-rail-item').length).toBe(12);
    expect(byTestId(mounted.container, 'tools-panel-single-page')).not.toBeNull();
    expect(byTestId(mounted.container, 'single-page-analyzer')).not.toBeNull();
  });

  it('navigates to every instrument via the rail', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<ToolsWorkspace />, services);
    for (const key of TOOL_CATALOG_KEYS) {
      click(byTestId(mounted.container, `tools-rail-${key}`)!);
      await waitFor(() => byTestId(mounted!.container, `tools-panel-${key}`) !== null);
      expect(byTestId(mounted.container, `tools-rail-${key}`)!.getAttribute('data-active')).toBe('true');
    }
  });

  it('honors the initialTool prop', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<ToolsWorkspace initialTool="serp" />, services);
    await flush();
    expect(byTestId(mounted.container, 'tools-panel-serp')).not.toBeNull();
    expect(byTestId(mounted.container, 'tool-serp')).not.toBeNull();
  });
});

describe('ToolsView shell mount', () => {
  it('mounts the workspace inside the shell tools frame', async () => {
    const services = toolServices().services;
    mounted = await renderTool(<ToolsView />, services);
    expect(byTestId(mounted.container, 'tools-view')).not.toBeNull();
    expect(byTestId(mounted.container, 'tools-workspace')).not.toBeNull();
    expect(byTestId(mounted.container, 'single-page-analyzer')).not.toBeNull();
  });
});