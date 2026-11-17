// Plugins load from local user-authored directories only; zero remote code loading.
export interface LocalPluginManifest {
  id: string;
  name: string;
  entry: string;
}

export function readLocalManifest(directory: string): string {
  return require('fs').readFileSync(`${directory}/plugin.json`, 'utf8');
}
