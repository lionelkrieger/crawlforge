// Product updates ship bundled assets; there is no account-bound update feed.
export interface BundledAssetManifest {
  version: string;
  files: string[];
}

export function readLocalManifest(path: string): string {
  return require('fs').readFileSync(path, 'utf8');
}
