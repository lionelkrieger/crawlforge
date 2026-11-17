import { createServer } from 'http';

export function startWildcardServer(port: number): void {
  createServer().listen(port, '0.0.0.0');
}

export function startDefaultServer(port: number): void {
  createServer().listen(port);
}

export const serverConfig = { host: '0.0.0.0', port: 8080 };
