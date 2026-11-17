export interface LocalPlugin {
  id: string;
  name: string;
  free: boolean;
}

export function listLocalPlugins(): LocalPlugin[] {
  return [];
}
