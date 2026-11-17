// Limits resolve locally: per-audit override -> project default -> app default -> built-in profile.
export interface CrawlLimits {
  totalPages: number | null;
  perDomain: number | null;
  concurrency: number;
}

export const defaultLimits: CrawlLimits = { totalPages: null, perDomain: null, concurrency: 8 };
