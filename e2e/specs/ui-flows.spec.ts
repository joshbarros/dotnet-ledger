import { test, expect, Page } from '@playwright/test';
import { ADMIN, signIn } from './support';

const run = () => Date.now().toString(36).toUpperCase() + Math.floor(Math.random() * 1000);

async function openAccount(page: Page, code: string, opening: number, shown: string) {
  await signIn(page, ADMIN);
  await page.getByLabel('Account code').fill(code);
  await page.getByLabel('Opening balance (cents)').fill(String(opening));
  await page.getByRole('button', { name: 'Open account' }).click();
  await expect(page.getByRole('heading', { name: `Send a payment from ${code}` })).toBeVisible();
  await expect(page.getByTestId('selected-remaining')).toHaveText(shown);
}

async function submit(page: Page, reference: string, quantity: number) {
  await page.getByLabel('Payment reference').fill(reference);
  await page.getByLabel('Amount (cents)', { exact: true }).fill(String(quantity));
  await page.getByRole('button', { name: 'Send payment' }).click();
}

const row = (page: Page, reference: string) => page.locator(`tr[data-reference="${reference}"]`);

test('the API refuses bad input and the page says why', async ({ page }) => {
  const id = run();
  await openAccount(page, `UI-ERR-${id}`, 100000, '$1,000.00');

  await submit(page, `PAY-${id}`, 5000001);
  await expect(page.getByRole('alert')).toContainText('at most 5000000 cents');

  await submit(page, '   ', 1000);
  await expect(page.getByRole('alert')).toContainText(/reference is required/i);

  await submit(page, `PAY-${id}`, 1000);
  await expect(row(page, `PAY-${id}`).getByTestId('status')).toHaveText('Settled', {
    timeout: 20_000,
  });
  await submit(page, `PAY-${id}`, 1000);
  await expect(page.getByRole('alert')).toContainText('already');
  await expect(page.locator('tbody tr[data-reference]')).toHaveCount(1);
});

test('a poison reference is rejected, gets no document, and the amount comes back', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-POI-${id}`, 50000, '$500.00');

  await submit(page, `PAY-POISON-${id}`, 20000);
  const poisoned = row(page, `PAY-POISON-${id}`);
  await expect(poisoned.getByTestId('status')).toHaveText('Rejected', { timeout: 20_000 });
  await expect(poisoned).toContainText('poison');
  await expect(poisoned.getByTestId('receipt')).toHaveText('—');
  await expect(poisoned.getByRole('button', { name: 'Reverse' })).toHaveCount(0);
  await expect(page.getByTestId('selected-remaining')).toHaveText('$500.00');
});

test('the last of the scarce number goes to one request and the other is told it is short', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-LAST-${id}`, 10100, '$101.00');

  await submit(page, `PAY-A-${id}`, 10000);
  await submit(page, `PAY-B-${id}`, 10000);

  const statuses = page.locator('tbody tr[data-reference] [data-testid="status"]');
  await expect(statuses).toHaveCount(2);
  await expect
    .poll(async () => (await statuses.allTextContents()).map((s) => s.trim()).sort(), {
      timeout: 20_000,
    })
    .toEqual(['Settled', 'Rejected'].sort());
  await expect(page.locator('tbody')).toContainText('Short');
  await expect(page.getByTestId('selected-remaining')).toHaveText('$0.00');
});

test('the event log shows the Kafka steps and the reversal keeps the document', async ({
  page,
}) => {
  const id = run();
  await openAccount(page, `UI-LOG-${id}`, 40000, '$400.00');

  await submit(page, `PAY-${id}`, 20000);
  const mine = row(page, `PAY-${id}`);
  await expect(mine.getByTestId('status')).toHaveText('Settled', { timeout: 20_000 });

  await mine.getByRole('button', { name: 'Event log' }).click();
  const log = page.getByRole('region', { name: 'Kafka event log' });
  await expect(log).toContainText('funds.reserve', { timeout: 20_000 });
  await expect(log).toContainText('funds.held');
  await expect(log).toContainText('receipt.issued');

  await mine.getByRole('button', { name: 'Reverse' }).click();
  await expect(mine.getByTestId('status')).toHaveText('Reversed');
  await expect(mine.getByRole('button', { name: 'Reverse' })).toHaveCount(0);
  await expect(mine.getByTestId('receipt')).toHaveText(/RC-[0-9A-F]{12}/);
  await expect(page.getByTestId('selected-remaining')).toHaveText('$400.00');
  await expect(log).toContainText('funds.released', { timeout: 20_000 });
});
