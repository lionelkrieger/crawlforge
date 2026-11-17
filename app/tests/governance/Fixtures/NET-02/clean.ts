export async function fetchLocalApi(path: string): Promise<string> {
  const response = await fetch(`/api/${path}`);
  return response.text();
}

export async function checkBackendHealth(port: number): Promise<string> {
  const response = await fetch(`http://127.0.0.1:${port}/api/health`);
  return response.text();
}
