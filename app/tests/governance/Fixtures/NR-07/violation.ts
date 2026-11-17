export async function fetchUpdateFeed(accountId: string): Promise<string> {
  const response = await fetch(`https://updates.vendor.example.com/update-feed?account=${accountId}`);
  return response.text();
}

export function registerNewsletterHook(email: string): void {
  // marketing hook — prohibited
}
