import { defineConfig, devices } from '@playwright/test';
import { apiDirectory, apiEnvironment, E2E_API_URL, E2E_UI_URL } from './stack';

/**
 * End-to-end tests against the real stack in mock-identity mode: Angular dev server → API → SQL Server, with the
 * background worker delivering notifications. Uses its own ports (UI 4300, API 5199 over HTTP), so it never touches a
 * development stack running on 4200/7143.
 *
 * Requires: a migrated and seeded database in E2E_CONNECTION_STRING, and a built API and worker
 * (dotnet build -c Release). Playwright starts the API and UI below; global-setup starts the worker.
 */
export default defineConfig({
  testDir: '.',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false, // the journeys share one database and switch personas; run them in order
  workers: 1,
  forbidOnly: !!process.env['CI'],
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never', outputFolder: '../playwright-report' }]] : 'list',
  outputDir: '../test-results',
  globalSetup: './global-setup.ts',
  use: {
    baseURL: E2E_UI_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet FinOps.Api.dll --urls ' + E2E_API_URL,
      cwd: apiDirectory,
      env: apiEnvironment(),
      url: `${E2E_API_URL}/health/ready`,
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command: 'npx ng serve --host 127.0.0.1 --port 4300 --proxy-config e2e/proxy.e2e.json',
      cwd: '..',
      url: E2E_UI_URL,
      reuseExistingServer: false,
      timeout: 180_000,
    },
  ],
});
