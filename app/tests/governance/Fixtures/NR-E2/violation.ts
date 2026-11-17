export async function fetchFingerprintUpdates(): Promise<void> {
  await fetch('https://cdn.vendor.example.com/fingerprints/latest.json');
}
