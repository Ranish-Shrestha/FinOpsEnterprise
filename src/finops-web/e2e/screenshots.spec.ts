import { expect, Page, test } from '@playwright/test';
import { join } from 'node:path';

/**
 * Regenerates the README screenshots in docs/screenshots from the running E2E stack (synthetic data only).
 * Skipped unless SCREENSHOTS=1, so normal E2E runs never rewrite tracked files:
 *   $env:SCREENSHOTS = "1"; npm run e2e -- screenshots
 * Run after the journeys so the panels have data.
 */
test.skip(process.env['SCREENSHOTS'] !== '1', 'Set SCREENSHOTS=1 to regenerate docs/screenshots.');
test.use({ viewport: { width: 1440, height: 1000 } });

const out = (name: string) => join(__dirname, '..', '..', '..', 'docs', 'screenshots', name);

async function signInAs(page: Page, label: string, name: string): Promise<void> {
  if (page.url() === 'about:blank') await page.goto('/login');
  await page.getByLabel('Development persona').selectOption({ label });
  const continueButton = page.getByRole('button', { name: 'Continue' });
  if (await continueButton.isVisible()) await continueButton.click();
  await expect(page.locator('.user-name')).toHaveText(name);
  await page.waitForLoadState('networkidle');
}

async function openPage(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await page.waitForLoadState('networkidle');
}

test('README screenshots', async ({ page }) => {
  await signInAs(page, 'Demo Employee (Engineering)', 'Demo Employee');
  await page.screenshot({ path: out('overview.png') });
  await openPage(page, '/notifications');
  await page.getByRole('region', { name: /^Notifications/ }).screenshot({ path: out('notifications.png') });

  await signInAs(page, 'Demo Engineering Manager (approves Engineering)', 'Demo Engineering Manager');
  await openPage(page, '/expenses');
  const expenses = page.getByRole('region', { name: 'Expenses' });
  await expenses.getByRole('tab', { name: 'Approval queue' }).click();
  await expenses.getByRole('table').getByRole('button').first().click();
  await expect(expenses.getByRole('complementary')).toBeVisible();
  await expenses.screenshot({ path: out('approval-queue.png') });

  await signInAs(page, 'Demo Finance Officer (posts approved expenses)', 'Demo Finance Officer');
  await openPage(page, '/budgets');
  await page.getByRole('region', { name: 'Budget vs. actual' }).screenshot({ path: out('budgets.png') });
  await openPage(page, '/reports');
  const reports = page.getByRole('region', { name: 'Reports' });
  await reports.getByRole('button', { name: /Department expense summary/ }).click();
  await reports.getByRole('button', { name: 'Preview' }).click();
  await expect(reports.getByRole('table')).toBeVisible();
  await reports.screenshot({ path: out('reports.png') });

  await signInAs(page, 'Demo Administrator (reads all, audit)', 'Demo Administrator');
  await openPage(page, '/admin/outbox');
  await page.getByRole('region', { name: /^Outbox/ }).screenshot({ path: out('outbox.png') });
});
