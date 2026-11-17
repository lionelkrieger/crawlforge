export function resolveQuota(licenseTier: string): number {
  return licenseTier === 'enterprise' ? 1000000 : 500;
}

export const featureGate = { gatedFeature: true };
