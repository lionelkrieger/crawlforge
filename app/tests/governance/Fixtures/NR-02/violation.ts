export interface LicenseState {
  licenseKey: string;
  subscriptionTier: string;
  trialPeriodDays: number;
}

export async function validateLicense(key: string): Promise<boolean> {
  const response = await fetch('https://vendor.example.com/license/validate', {
    headers: { 'X-License-Key': key },
  });
  return response.ok;
}
