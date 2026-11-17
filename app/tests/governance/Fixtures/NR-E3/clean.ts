// WAF challenge detection is diagnostic only (classification labels, no solving).
export interface ChallengeDetection {
  provider: string;
  isChallenge: boolean;
}

export function detectChallenge(body: string): ChallengeDetection {
  return { provider: body.includes('cf-chl') ? 'Cloudflare' : 'Unknown', isChallenge: body.includes('cf-chl') };
}
