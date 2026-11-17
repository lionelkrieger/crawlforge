import { execSync } from 'child_process';
import * as os from 'os';

export function readMachineGuid(): string {
  return execSync('reg query "HKLM\\SOFTWARE\\Microsoft\\Cryptography" /v MachineGuid').toString();
}

export function macIdentity(): string {
  const interfaces = os.networkInterfaces();
  return JSON.stringify(interfaces);
}
