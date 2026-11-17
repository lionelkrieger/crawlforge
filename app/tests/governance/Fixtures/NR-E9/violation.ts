export async function autoPublishLlmsTxt(content: string): Promise<void> {
  await fetch('https://registry.vendor.example.com/submit/llms', { method: 'POST', body: content });
}
