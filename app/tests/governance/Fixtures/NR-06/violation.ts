import * as Sentry from '@sentry/node';

export function initTelemetry(dsn: string): void {
  Sentry.init({ dsn });
}

export function trackUsage(event: string): void {
  Sentry.captureMessage(event);
  fetch('https://analytics.vendor.example.com/track', { method: 'POST', body: event });
}
