import OpenAI from 'openai';

export async function embedRemote(text: string): Promise<number[]> {
  const client = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });
  const response = await client.embeddings.create({ model: 'text-embedding-3-small', input: text });
  return response.data[0].embedding;
}

export async function embedViaRemoteDaemon(text: string): Promise<number[]> {
  const response = await fetch('http://192.168.1.50:11434/api/embed', { method: 'POST', body: text });
  return response.json();
}
