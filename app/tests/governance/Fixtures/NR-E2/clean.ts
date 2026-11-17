import { readFileSync } from 'fs';

// Bundled catalogue; updated only with product updates.
export function loadBundledSignatures(assetsRoot: string): string {
  return readFileSync(`${assetsRoot}/waf-signatures/signatures.json`, 'utf8');
}
