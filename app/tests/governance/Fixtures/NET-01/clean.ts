declare const server: { listen(port: number, host: string): void };

export function startLoopbackServer(port: number): void {
  server.listen(port, '127.0.0.1');
}

export const serverConfig = { host: '127.0.0.1', port: 5173 };
