import { randomBytes, randomUUID } from 'crypto';

// App-generated random entropy only; no OS identity stores are read.
export function createSecret(bytes: number): Buffer {
  return randomBytes(bytes);
}

export function createLocalToken(): string {
  return randomUUID();
}
