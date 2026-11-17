/**
 * HOTFIX-01: per-surface error boundary.
 *
 * A failing report/run panel must show an inline error and keep the rest of
 * the desktop shell alive. React unmounts the whole tree on an uncaught render
 * error, so every mounted view is wrapped in one of these boundaries.
 */
import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button, Paper, Text } from '@mantine/core';

interface Props {
  readonly children: ReactNode;
  /** Human-readable surface name used in the inline error. */
  readonly label?: string;
}

interface State {
  readonly error: Error | null;
}

export class SurfaceErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Local diagnostic only — never leaves the loopback boundary.
    console.error(`[crawlforge] ${this.props.label ?? 'Surface'} render failed`, error, info.componentStack);
  }

  render(): ReactNode {
    const { error } = this.state;
    if (!error) {
      return this.props.children;
    }

    return (
      <Paper withBorder p="md" role="alert" data-testid="surface-error">
        <Text fw={600} c="var(--cf-danger)">{this.props.label ?? 'Surface'} could not render</Text>
        <Text size="sm" c="dimmed" style={{ wordBreak: 'break-word' }}>{error.message}</Text>
        <Button
          size="compact-xs"
          variant="subtle"
          mt="xs"
          onClick={() => this.setState({ error: null })}
        >
          Retry
        </Button>
      </Paper>
    );
  }
}
