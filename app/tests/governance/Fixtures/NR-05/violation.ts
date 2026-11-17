import { initialize } from 'launchdarkly-js-client-sdk';

export async function resolveRemoteFlags(flagKey: string): Promise<boolean> {
  const client = initialize('env-key', { bootstrap: 'localStorage' });
  return client.variation(flagKey, false);
}
