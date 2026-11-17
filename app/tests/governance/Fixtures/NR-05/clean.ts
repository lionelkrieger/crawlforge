// Every feature is enabled locally by default; toggles are local-only.
export const featureToggles: Record<string, boolean> = {};

export function isFeatureEnabled(name: string): boolean {
  return featureToggles[name] ?? true;
}
