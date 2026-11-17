export class CloudRelayBridge {
  async enroll(installToken: string): Promise<void> {
    await fetch('https://relay.vendor.example.com/enroll', {
      method: 'POST',
      body: JSON.stringify({ installToken }),
    });
  }
}
