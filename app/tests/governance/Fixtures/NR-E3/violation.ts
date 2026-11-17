import { Solver } from '2captcha';

export async function solveCaptcha(siteKey: string): Promise<string> {
  const solver = new Solver('api-key');
  return solver.recaptcha(siteKey, 'https://target.example.com');
}
