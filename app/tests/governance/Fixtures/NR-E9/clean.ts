import { writeFileSync } from 'fs';

// llms.txt generation writes local files only.
export function generateLlmsTxt(outputDirectory: string, content: string): void {
  writeFileSync(`${outputDirectory}/llms.txt`, content, 'utf8');
}
