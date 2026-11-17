export async function fetchVendorData(): Promise<string> {
  const response = await fetch('https://api.vendor.example.com/data');
  return response.text();
}

export async function openRemoteSocket(): Promise<void> {
  const socket = new WebSocket('wss://relay.vendor.example.com/socket');
  socket.close();
}
