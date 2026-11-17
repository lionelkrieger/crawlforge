/**
 * Bundled JSON schema asset tests: the asset ships at assets/plugin-schema,
 * parses as draft 2020-12, and is the same object the validator module
 * imports (single source of truth — no drift).
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { PLUGIN_MANIFEST_SCHEMA } from '../../../src/frontend/src/plugins/manifestSchema';

function resolveAssetPath(): string {
  // tests/frontend/plugins → app root is three levels up.
  const here = fileURLToPath(new URL('.', import.meta.url));
  return join(here, '..', '..', '..', 'assets', 'plugin-schema', 'plugin.schema.json');
}

describe('plugin.schema.json asset', () => {
  it('exists and parses as JSON', () => {
    const raw = readFileSync(resolveAssetPath(), 'utf8');
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    expect(parsed.$schema).toContain('json-schema.org');
    expect(parsed.type).toBe('object');
  });

  it('is the exact schema the validator imports', () => {
    const raw = readFileSync(resolveAssetPath(), 'utf8');
    expect(JSON.parse(raw)).toEqual(PLUGIN_MANIFEST_SCHEMA);
  });

  it('locks the required fields and hook enum', () => {
    const schema = PLUGIN_MANIFEST_SCHEMA as {
      required: string[];
      properties: Record<string, { enum?: string[]; items?: { enum?: string[] } }>;
    };
    expect(schema.required).toEqual(['id', 'name', 'version', 'description', 'engine', 'entry']);
    expect(schema.properties.hooks.items?.enum).toEqual(['onLoad', 'onTabActivate', 'onDataUpdate', 'onCrawlComplete']);
    expect(schema.properties.permissions).toBeDefined();
    expect(schema.properties.contributes).toBeDefined();
  });
});