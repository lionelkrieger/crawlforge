export async function installRemotePlugin(registryUrl: string): Promise<void> {
  const module = await import(`${registryUrl}/plugin-bundle.js`);
  module.register();
}

export async function browsePluginMarketplace(): Promise<string[]> {
  const response = await fetch('https://marketplace.vendor.example.com/plugins/marketplace');
  return response.json();
}
