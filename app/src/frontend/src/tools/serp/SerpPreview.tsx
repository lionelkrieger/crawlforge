/**
 * Live SERP & Social Card Pixel Preview (FS-CRAWL-002 §15.6, criterion #48).
 *
 * The preview is the instrument: a Google-class snippet and two social
 * cards re-render on every keystroke from bundled font metrics and
 * configured pixel budgets. Overflow is highlighted inline (the cut tail
 * stays visible, tinted), and each field carries a used/budget pixel meter.
 * Desktop/mobile toggle swaps the budget set. Everything is local — the
 * card image accepts a data: URL only; no remote content is ever fetched.
 *
 * @module tools/serp/SerpPreview
 */

import { useMemo, useState, type CSSProperties } from 'react';
import {
  Badge,
  Box,
  Button,
  Group,
  NumberInput,
  Paper,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Textarea,
  Title,
  Tooltip
} from '@mantine/core';
import { IconGlobe, IconRefresh, IconSearch } from '../../theme/icons';
import { ToolFrame } from '../shared/ToolFrame';
import './serp.css';
import {
  CARD_DESCRIPTION_FONT,
  CARD_TITLE_FONT,
  DESKTOP_BUDGET,
  MOBILE_BUDGET,
  SOCIAL_BUDGETS,
  TITLE_FONT,
  URL_FONT,
  analyzeSerp,
  measureTextPx,
  truncateToWidthPx,
  wrapToLines,
  type SerpBudget
} from './fontMetrics';

/** SerpPreview props (seed values for embedding surfaces). */
export interface SerpPreviewProps {
  readonly initialTitle?: string;
  readonly initialDescription?: string;
  readonly initialUrl?: string;
}

const DEFAULT_TITLE = 'CrawlForge — Local-First Website Crawler & Technical SEO Auditor';
const DEFAULT_DESCRIPTION =
  'CrawlForge is an offline-first desktop crawler that audits entire websites locally: technical SEO, content quality, accessibility, performance, and structured data — with no accounts, no telemetry, and no vendor services. Your data never leaves the machine.';
const DEFAULT_URL = 'https://crawlforge.local/features/auditor';

/** Used/budget pixel meter with over-budget heat. */
function PixelMeter({ used, budget, testId }: { used: number; budget: number; testId: string }) {
  const fraction = budget > 0 ? Math.min(1.35, used / budget) : 0;
  const over = used > budget;
  return (
    <Group gap={8} wrap="nowrap" data-testid={testId}>
      <Box className="cf-meter-track" flex={1}>
        <Box
          className="cf-meter-fill"
          data-over={over}
          style={{ width: `${Math.min(100, (fraction / 1.35) * 100)}%` }}
        />
        <Box className="cf-meter-budget-mark" style={{ left: `${(1 / 1.35) * 100}%` }} />
      </Box>
      <Text size="xs" ff="monospace" c={over ? 'var(--cf-danger)' : 'dimmed'} style={{ fontVariantNumeric: 'tabular-nums' }}>
        {Math.round(used)} / {budget} px
      </Text>
    </Group>
  );
}

/** Inline text with the overflow tail highlighted (live truncation mark). */
function TruncatedText({
  kept,
  overflow,
  keptStyle,
  testId
}: {
  kept: string;
  overflow: string;
  keptStyle: CSSProperties;
  testId: string;
}) {
  return (
    <span style={keptStyle} data-testid={testId}>
      {kept}
      {overflow ? (
        <Tooltip label={`Truncated — ${overflow.length} characters cut`} openDelay={200}>
          <span className="cf-serp-overflow" data-testid={`${testId}-overflow`}>
            {overflow}
          </span>
        </Tooltip>
      ) : null}
    </span>
  );
}

/** The Google-class snippet on a white results card. */
function SerpSnippet({
  title,
  description,
  url,
  budget,
  device
}: {
  title: string;
  description: string;
  url: string;
  budget: SerpBudget;
  device: 'desktop' | 'mobile';
}) {
  const analysis = useMemo(() => analyzeSerp(title, description, budget), [title, description, budget]);
  const urlWidth = measureTextPx(url, URL_FONT);
  const host = useMemo(() => {
    try {
      return new URL(url).host;
    } catch {
      return url;
    }
  }, [url]);

  return (
    <Box className="cf-serp-card" data-device={device} data-testid="serp-snippet">
      <Group gap={8} wrap="nowrap" mb={6}>
        <span className="cf-serp-favicon" aria-hidden>
          <IconGlobe size={13} />
        </span>
        <Stack gap={0}>
          <Text size="xs" style={{ color: '#202124', lineHeight: 1.2 }}>
            {host}
          </Text>
          <Text size="xs" style={{ color: '#5f6368', lineHeight: 1.2 }} ff="monospace" data-testid="serp-url">
            {url}
          </Text>
        </Stack>
      </Group>
      <TruncatedText
        testId="serp-title-render"
        kept={analysis.title.text}
        overflow={analysis.title.overflow}
        keptStyle={{
          display: 'block',
          color: '#1a0dab',
          fontFamily: 'arial, sans-serif',
          fontSize: TITLE_FONT.sizePx,
          lineHeight: '26px',
          cursor: 'pointer'
        }}
      />
      <Box mt={4} data-testid="serp-description-render">
        {analysis.description.lines.map((line, index) => (
          <Text
            key={index}
            component="div"
            style={{
              color: '#4d5156',
              fontFamily: 'arial, sans-serif',
              fontSize: 14,
              lineHeight: '20px'
            }}
          >
            {line.text}
            {index === analysis.description.lines.length - 1 && analysis.description.overflow ? (
              <span className="cf-serp-overflow" data-testid="serp-description-overflow">
                {analysis.description.overflow}
              </span>
            ) : null}
          </Text>
        ))}
      </Box>
      <Stack gap={6} mt={10}>
        <PixelMeter used={analysis.title.widthPx} budget={budget.titleMaxPx} testId="serp-title-meter" />
        <PixelMeter
          used={analysis.description.totalWidthPx}
          budget={budget.descriptionMaxPx}
          testId="serp-description-meter"
        />
        <Group gap={6}>
          {analysis.title.truncated ? (
            <Badge size="xs" color="red" variant="light" data-testid="serp-title-truncated">
              Title truncated
            </Badge>
          ) : (
            <Badge size="xs" color="green" variant="light" data-testid="serp-title-fits">
              Title fits
            </Badge>
          )}
          {analysis.description.truncated ? (
            <Badge size="xs" color="red" variant="light" data-testid="serp-description-truncated">
              Description truncated
            </Badge>
          ) : (
            <Badge size="xs" color="green" variant="light" data-testid="serp-description-fits">
              Description fits
            </Badge>
          )}
          <Badge size="xs" color="steel" variant="outline" data-testid="serp-url-width">
            URL {Math.round(urlWidth)} px
          </Badge>
        </Group>
      </Stack>
    </Box>
  );
}

/** One social card (X summary or Facebook link) with its own budgets. */
function SocialCard({
  platform,
  title,
  description,
  url,
  image
}: {
  platform: 'twitter' | 'facebook';
  title: string;
  description: string;
  url: string;
  image: string;
}) {
  const budget = SOCIAL_BUDGETS[platform];
  const titleLines = platform === 'facebook' ? 2 : 1;
  const titleWrap = useMemo(
    () =>
      titleLines > 1
        ? wrapToLines(title, budget.titleMaxPx / titleLines, titleLines, CARD_TITLE_FONT)
        : null,
    [title, budget.titleMaxPx, titleLines]
  );
  const titleFit = useMemo(
    () => (titleWrap ? null : truncateToWidthPx(title, budget.titleMaxPx, CARD_TITLE_FONT)),
    [title, budget.titleMaxPx, titleWrap]
  );
  const descWrap = useMemo(
    () =>
      wrapToLines(
        description,
        budget.descriptionMaxPx / budget.descriptionLines,
        budget.descriptionLines,
        CARD_DESCRIPTION_FONT
      ),
    [description, budget]
  );
  const host = useMemo(() => {
    try {
      return new URL(url).host.toUpperCase();
    } catch {
      return url;
    }
  }, [url]);
  const titleTruncated = titleWrap ? titleWrap.truncated : (titleFit?.truncated ?? false);
  const hasImage = image.startsWith('data:image/');

  return (
    <Box className="cf-social-card" data-platform={platform} data-testid={`social-card-${platform}`}>
      <Box className="cf-social-image" aria-hidden>
        {hasImage ? (
          <img src={image} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
        ) : (
          <Group justify="center" align="center" h="100%" gap={6}>
            <IconGlobe size={20} color="var(--mantine-color-night-3)" />
            <Text size="xs" c="dimmed">
              og:image (data: URL)
            </Text>
          </Group>
        )}
      </Box>
      <Box p={10}>
        <Text size="xs" c="dimmed" tt="uppercase" lts={0.6} mb={4} data-testid={`social-host-${platform}`}>
          {host}
        </Text>
        <Box
          style={{
            color: '#0f1419',
            fontFamily: 'arial, sans-serif',
            fontSize: 16,
            fontWeight: 600,
            lineHeight: '20px'
          }}
          data-testid={`social-title-${platform}`}
        >
          {titleWrap
            ? titleWrap.lines.map((line, index) => (
                <span key={index} style={{ display: 'block' }}>
                  {line.text}
                  {index === titleWrap.lines.length - 1 && titleWrap.overflow ? (
                    <span className="cf-serp-overflow">{titleWrap.overflow}</span>
                  ) : null}
                </span>
              ))
            : titleFit && (
                <TruncatedText
                  testId={`social-title-${platform}-render`}
                  kept={titleFit.text}
                  overflow={titleFit.overflow}
                  keptStyle={{}}
                />
              )}
        </Box>
        <Box
          mt={4}
          style={{
            color: '#536471',
            fontFamily: 'arial, sans-serif',
            fontSize: 14,
            lineHeight: '18px'
          }}
          data-testid={`social-description-${platform}`}
        >
          {descWrap.lines.map((line, index) => (
            <span key={index} style={{ display: 'block' }}>
              {line.text}
              {index === descWrap.lines.length - 1 && descWrap.overflow ? (
                <span className="cf-serp-overflow">{descWrap.overflow}</span>
              ) : null}
            </span>
          ))}
        </Box>
        <Group gap={6} mt={8}>
          <Badge size="xs" color={titleTruncated ? 'red' : 'green'} variant="light" data-testid={`social-title-verdict-${platform}`}>
            {titleTruncated ? 'Title cut' : 'Title fits'}
          </Badge>
          <Badge size="xs" color={descWrap.truncated ? 'red' : 'green'} variant="light" data-testid={`social-description-verdict-${platform}`}>
            {descWrap.truncated ? 'Description cut' : 'Description fits'}
          </Badge>
        </Group>
      </Box>
    </Box>
  );
}

/** The live SERP & Social Card preview tool. */
export function SerpPreview({ initialTitle, initialDescription, initialUrl }: SerpPreviewProps) {
  const [title, setTitle] = useState(initialTitle ?? DEFAULT_TITLE);
  const [description, setDescription] = useState(initialDescription ?? DEFAULT_DESCRIPTION);
  const [url, setUrl] = useState(initialUrl ?? DEFAULT_URL);
  const [image, setImage] = useState('');
  const [device, setDevice] = useState<'desktop' | 'mobile'>('desktop');
  const [desktopBudget, setDesktopBudget] = useState<SerpBudget>(DESKTOP_BUDGET);
  const [mobileBudget, setMobileBudget] = useState<SerpBudget>(MOBILE_BUDGET);

  const budget = device === 'desktop' ? desktopBudget : mobileBudget;
  const setBudget = device === 'desktop' ? setDesktopBudget : setMobileBudget;

  return (
    <ToolFrame
      testId="tool-serp"
      icon={<IconSearch size={17} />}
      title="SERP & Social Card Preview"
      spec="criterion #48"
      subtitle="Live pixel truncation from bundled font metrics — no network, no remote fonts."
      actions={
        <SegmentedControl
          size="xs"
          value={device}
          onChange={(value) => setDevice(value as 'desktop' | 'mobile')}
          data={[
            { label: 'Desktop', value: 'desktop' },
            { label: 'Mobile', value: 'mobile' }
          ]}
          data-testid="serp-device"
        />
      }
    >
      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
        <Stack gap="sm">
          <TextInput
            label="Title"
            value={title}
            onChange={(event) => setTitle(event.currentTarget.value)}
            data-testid="serp-title-input"
            size="sm"
          />
          <Textarea
            label="Description"
            value={description}
            onChange={(event) => setDescription(event.currentTarget.value)}
            autosize
            minRows={3}
            maxRows={6}
            data-testid="serp-description-input"
            size="sm"
          />
          <TextInput
            label="Display URL"
            value={url}
            onChange={(event) => setUrl(event.currentTarget.value)}
            ff="monospace"
            data-testid="serp-url-input"
            size="sm"
          />
          <TextInput
            label="Card image (data: URL only — offline)"
            placeholder="data:image/png;base64,…"
            value={image}
            onChange={(event) => setImage(event.currentTarget.value)}
            ff="monospace"
            data-testid="serp-image-input"
            size="sm"
          />
          <Paper withBorder p="sm" className="cf-budget-panel">
            <Group justify="space-between" mb={8}>
              <Text size="xs" fw={600} tt="uppercase" lts={0.5} c="dimmed">
                {device} pixel budgets
              </Text>
              <Button
                size="compact-xs"
                variant="subtle"
                color="steel"
                leftSection={<IconRefresh size={12} />}
                onClick={() => setBudget(device === 'desktop' ? DESKTOP_BUDGET : MOBILE_BUDGET)}
                data-testid="serp-budget-reset"
              >
                Defaults
              </Button>
            </Group>
            <Group grow>
              <NumberInput
                label="Title max px"
                size="xs"
                min={80}
                max={2000}
                value={budget.titleMaxPx}
                onChange={(value) => setBudget({ ...budget, titleMaxPx: Number(value) || budget.titleMaxPx })}
                data-testid="serp-budget-title"
              />
              <NumberInput
                label="Description total px"
                size="xs"
                min={80}
                max={4000}
                value={budget.descriptionMaxPx}
                onChange={(value) =>
                  setBudget({ ...budget, descriptionMaxPx: Number(value) || budget.descriptionMaxPx })
                }
                data-testid="serp-budget-description"
              />
              <NumberInput
                label="Desc. lines"
                size="xs"
                min={1}
                max={4}
                value={budget.descriptionLines}
                onChange={(value) =>
                  setBudget({ ...budget, descriptionLines: Math.max(1, Number(value) || budget.descriptionLines) })
                }
                data-testid="serp-budget-lines"
              />
            </Group>
          </Paper>
        </Stack>
        <Stack gap="sm">
          <SerpSnippet title={title} description={description} url={url} budget={budget} device={device} />
          <Title order={5} mt={4}>
            Social cards
          </Title>
          <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
            <SocialCard platform="twitter" title={title} description={description} url={url} image={image} />
            <SocialCard platform="facebook" title={title} description={description} url={url} image={image} />
          </SimpleGrid>
        </Stack>
      </SimpleGrid>
    </ToolFrame>
  );
}