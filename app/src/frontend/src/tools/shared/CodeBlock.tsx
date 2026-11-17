/**
 * Monospace code/output block with a local copy action. Used for robots
 * bodies, llms.txt output, selectors, XPath, and JSON rule trees. Clipboard
 * is best-effort (the button degrades to a manual-select hint when the
 * async clipboard API is unavailable, e.g. non-secure contexts).
 *
 * @module tools/shared/CodeBlock
 */

import { useState } from 'react';
import { ActionIcon, Box, CopyButton, Group, Text, Tooltip } from '@mantine/core';
import { IconCheck, IconCommand } from '../../theme/icons';

/** CodeBlock props. */
export interface CodeBlockProps {
  /** The code/output text. */
  readonly code: string;
  /** Optional caption above the block. */
  readonly label?: string;
  /** Maximum height before scrolling (px). */
  readonly maxHeight?: number;
  /** Test identifier root. */
  readonly testId?: string;
  /** Hide the copy action (default false). */
  readonly noCopy?: boolean;
}

/** A scrollable monospace block with copy affordance. */
export function CodeBlock({ code, label, maxHeight = 280, testId, noCopy = false }: CodeBlockProps) {
  const [copied, setCopied] = useState(false);
  return (
    <Box className="cf-codeblock" data-testid={testId}>
      {label || !noCopy ? (
        <Group justify="space-between" mb={4}>
          {label ? (
            <Text size="xs" c="dimmed" fw={600} tt="uppercase" lts={0.5}>
              {label}
            </Text>
          ) : (
            <span />
          )}
          {!noCopy ? (
            <CopyButton value={code} timeout={1200}>
              {({ copy }) => (
                <Tooltip label={copied ? 'Copied' : 'Copy'} openDelay={300}>
                  <ActionIcon
                    size="sm"
                    variant="subtle"
                    color="steel"
                    aria-label="Copy to clipboard"
                    data-testid={testId ? `${testId}-copy` : undefined}
                    onClick={() => {
                      copy();
                      setCopied(true);
                      window.setTimeout(() => setCopied(false), 1200);
                    }}
                  >
                    {copied ? <IconCheck size={13} /> : <IconCommand size={13} />}
                  </ActionIcon>
                </Tooltip>
              )}
            </CopyButton>
          ) : null}
        </Group>
      ) : null}
      <pre className="cf-codeblock-pre" style={{ maxHeight }} data-testid={testId ? `${testId}-pre` : undefined}>
        {code}
      </pre>
    </Box>
  );
}