import { defineConfig } from '@playwright/test';

// End-to-end: real session server + two bridges in fake sim mode + the built UI in headless Edge.
// Uses the Edge that is installed on Windows (no browser download). CROSSCHECK_BROWSER=chromium uses
// Playwright's own Chromium instead (Linux, Codespaces: npx playwright install chromium).
export default defineConfig({
  testDir: 'e2e',
  timeout: 120_000,
  workers: 1,
  reporter: 'list',
  use: { ...(process.env.CROSSCHECK_BROWSER === 'chromium' ? {} : { channel: 'msedge' }), headless: true },
});
