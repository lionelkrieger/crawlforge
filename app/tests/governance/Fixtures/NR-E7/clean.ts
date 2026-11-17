// Loopback-only embedding daemon (Ollama class); non-loopback rejected at configuration.
export function validateEmbeddingEndpoint(endpoint: string): boolean {
  const url = new URL(endpoint);
  return url.hostname === '127.0.0.1' || url.hostname === 'localhost' || url.hostname === '::1';
}

export async function embedLocal(text: string): Promise<number[]> {
  const response = await fetch('http://127.0.0.1:11434/api/embed', {
    method: 'POST',
    body: JSON.stringify({ input: text }),
  });
  return response.json();
}
