import { expect, Page, test } from '@playwright/test';

/** Mock personas from src/app/auth.config.ts. */
const persona = {
  employee: { label: 'Demo Employee (Engineering)', name: 'Demo Employee', oid: '20000000-0000-0000-0000-000000000001' },
  engineeringManager: { label: 'Demo Engineering Manager (approves Engineering)', name: 'Demo Engineering Manager' },
  finance: { label: 'Demo Finance Officer (posts approved expenses)', name: 'Demo Finance Officer' },
  manager: { label: 'Demo Manager (Finance)', name: 'Demo Manager', oid: '20000000-0000-0000-0000-000000000002' },
} as const;

type Persona = (typeof persona)[keyof typeof persona];

/** First call: the /login page. Later calls: the persona picker in the workspace top bar. */
async function signInAs(page: Page, who: Persona): Promise<void> {
  if (page.url() === 'about:blank') await page.goto('/login');
  await page.getByLabel('Development persona').selectOption({ label: who.label });
  const continueButton = page.getByRole('button', { name: 'Continue' });
  if (await continueButton.isVisible()) await continueButton.click();
  await expect(page.locator('.user-name')).toHaveText(who.name);
}

/** Opens a workspace page from the sidebar. */
async function openPage(page: Page, name: string): Promise<void> {
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name, exact: true }).click();
}

function expensesPanel(page: Page) {
  return page.getByRole('region', { name: 'Expenses' });
}

/** Opens an expense from a tab of the Expenses panel and returns its detail pane. */
async function openFromTab(page: Page, tab: string, description: string) {
  const panel = expensesPanel(page);
  await panel.getByRole('tab', { name: tab }).click();
  await panel.getByRole('button', { name: description, exact: true }).click();
  const detail = panel.getByRole('complementary');
  await expect(detail.getByRole('heading', { name: description })).toBeVisible();
  return detail;
}

/** Yesterday in UTC: the API rejects future dates, and "today" in a browser time zone can be tomorrow in UTC. */
function yesterdayUtc(): string {
  return new Date(Date.now() - 86_400_000).toISOString().slice(0, 10);
}

test('an expense goes from employee to manager to finance, and the worker notifies and reimburses', async ({ page }) => {
  const description = `E2E taxi ${Date.now()}`;

  await signInAs(page, persona.employee);
  await openPage(page, 'Expenses');
  const panel = expensesPanel(page);
  await panel.getByRole('button', { name: 'New expense' }).click();
  const form = panel.getByRole('form', { name: 'Expense details' });
  await form.getByLabel('Amount (USD)').fill('37.25');
  await form.getByLabel('Category').selectOption('Travel');
  await form.getByLabel('Date').fill(yesterdayUtc());
  await form.getByLabel('Description').fill(description);
  await form.getByRole('button', { name: 'Create draft' }).click();
  const draft = panel.getByRole('complementary');
  await expect(draft.getByRole('heading', { name: description })).toBeVisible();
  await draft.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(draft.locator('.status')).toHaveText('Submitted');

  await signInAs(page, persona.engineeringManager);
  const review = await openFromTab(page, 'Approval queue', description);
  await review.getByRole('button', { name: 'Approve' }).click();
  await expect(review.locator('.status')).toHaveText('Approved');

  await signInAs(page, persona.finance);
  const posting = await openFromTab(page, 'Ready to post', description);
  await posting.getByRole('button', { name: 'Post to ledger' }).click();
  await expect(posting.locator('.status')).toHaveText('Posted');

  // Delivered asynchronously by FinOps.Worker through the outbox: refresh until it arrives.
  await signInAs(page, persona.employee);
  await openPage(page, 'Notifications');
  const notifications = page.getByRole('region', { name: /^Notifications/ });
  const mineNotified = notifications.getByRole('listitem').filter({ hasText: description });
  await expect(async () => {
    await notifications.getByRole('button', { name: 'Refresh' }).click();
    await expect(mineNotified.filter({ hasText: 'posted and a reimbursement is scheduled' })).toBeVisible({ timeout: 1_000 });
    await expect(mineNotified.filter({ hasText: 'was approved' })).toBeVisible({ timeout: 1_000 });
  }).toPass({ timeout: 30_000 });

  await openPage(page, 'Expenses');
  const mine = await openFromTab(page, 'My expenses', description);
  await expect(mine.getByText(/^Scheduled /)).toBeVisible();

  // The open expense is in the URL, so a refresh (or a shared link) shows it again.
  await expect(page).toHaveURL(/\/expenses\/[0-9a-f-]{36}$/);
  await page.reload();
  await expect(expensesPanel(page).getByRole('complementary').getByRole('heading', { name: description })).toBeVisible();
});

test('a manager sees only their own department, in the UI and at the API', async ({ page, request }) => {
  await signInAs(page, persona.manager);
  const departments = page.getByRole('region', { name: 'Sample departments' }).getByRole('listitem');
  await expect(departments).toHaveCount(1);
  await expect(departments).toContainText('Demo Finance');

  // The UI hides Engineering expenses; the API must refuse them too, and indistinguishably from "not found".
  const own = await request.get('/api/expenses?mine=true', { headers: { 'X-Development-User': persona.employee.oid } });
  expect(own.ok()).toBe(true);
  const engineeringExpense = (await own.json()).items[0] as { id: string };
  const asManager = await request.get(`/api/expenses/${engineeringExpense.id}`,
    { headers: { 'X-Development-User': persona.manager.oid } });
  expect(asManager.status()).toBe(404);
});

test('finance downloads the budget report as an Excel file', async ({ page }) => {
  await signInAs(page, persona.finance);
  await openPage(page, 'Reports');
  const reports = page.getByRole('region', { name: 'Reports' });
  await reports.getByRole('button', { name: /Budget vs\. actual/ }).click();
  await reports.getByRole('button', { name: 'Preview' }).click();
  await expect(reports.getByRole('table')).toBeVisible();

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    reports.getByRole('button', { name: 'Excel' }).click(),
  ]);
  expect(download.suggestedFilename()).toMatch(/^budget-vs-actual-\d{8}-\d{4}\.xlsx$/);
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  const file = Buffer.concat(chunks);
  expect(file.subarray(0, 2).toString()).toBe('PK'); // an .xlsx is a zip package
});

test('sign-in returns to the page that was asked for', async ({ page }) => {
  await page.goto('/notifications');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotifications$/);
  await page.getByLabel('Development persona').selectOption({ label: persona.employee.label });
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page).toHaveURL(/\/notifications$/);
  await expect(page.getByRole('region', { name: /^Notifications/ })).toBeVisible();
});

test('pages the caller may not use are hidden and redirect to the dashboard', async ({ page }) => {
  await page.goto('/budgets');
  await page.getByLabel('Development persona').selectOption({ label: persona.employee.label });
  await page.getByRole('button', { name: 'Continue' }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  const nav = page.getByRole('navigation', { name: 'Main navigation' });
  await expect(nav.getByRole('link', { name: 'Expenses' })).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Budgets' })).toHaveCount(0);
  await expect(nav.getByRole('link', { name: 'Outbox' })).toHaveCount(0);
});

test('signing out ends the session and protects the workspace', async ({ page }) => {
  await signInAs(page, persona.employee);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/signed-out$/);
  await page.goto('/expenses');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fexpenses$/);
});
