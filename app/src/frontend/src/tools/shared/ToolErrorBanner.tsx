/**
 * Tool error banner: envelope message plus the field → reasons map
 * (FS-CRAWL-001 §10.4 validation surface). Renders nothing when there is
 * no error, so tools can mount it unconditionally.
 *
 * @module tools/shared/ToolErrorBanner
 */

import { Alert, Stack, Text } from '@mantine/core';
import { IconWarning } from '../../theme/icons';

/** ToolErrorBanner props. */
export interface ToolErrorBannerProps {
  /** Envelope/transport error message. */
  readonly error: string | null;
  /** Field-level reasons (validation errors). */
  readonly fieldErrors?: Readonly<Record<string, readonly string[]>> | null;
  /** Test identifier. */
  readonly testId?: string;
}

/** The error alert with per-field reasons. */
export function ToolErrorBanner({ error, fieldErrors, testId = 'tool-error' }: ToolErrorBannerProps) {
  if (!error) return null;
  const fields = fieldErrors ? Object.entries(fieldErrors) : [];
  return (
    <Alert color="red" variant="light" icon={<IconWarning size={15} />} title="Tool error" data-testid={testId}>
      <Stack gap={4}>
        <Text size="sm">{error}</Text>
        {fields.map(([field, reasons]) => (
          <Text key={field} size="xs" c="dimmed" data-testid={`${testId}-field-${field}`}>
            <Text span fw={600} ff="monospace">
              {field}
            </Text>
            {': '}
            {reasons.join(' ')}
          </Text>
        ))}
      </Stack>
    </Alert>
  );
}