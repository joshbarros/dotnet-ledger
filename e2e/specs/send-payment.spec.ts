import { test, expect } from '@playwright/test';
import { ADMIN, adminToken, signIn } from './support';

test('open an account, send a payment, see the receipt, reverse it', async ({ page }) => {
  const run = Date.now().toString(36).toUpperCase();
  const code = `E2E-${run}`;
  const reference = `PAY-${run}`;

  await signIn(page, ADMIN);
  await page.getByLabel('Account code').fill(code);
  await page.getByLabel('Opening balance (cents)').fill('100000');
  await page.getByRole('button', { name: 'Open account' }).click();

  const remaining = page.getByTestId('selected-remaining');
  await expect(page.getByRole('heading', { name: `Send a payment from ${code}` })).toBeVisible();
  await expect(remaining).toHaveText('$1,000.00');

  await page.getByLabel('Payment reference').fill(reference);
  await page.getByLabel('Amount (cents)').fill('30000');
  await page.getByRole('button', { name: 'Send payment' }).click();

  const row = page.locator(`tr[data-reference="${reference}"]`);
  await expect(row.getByTestId('status')).toHaveText('Settled', { timeout: 20_000 });
  await expect(row.getByTestId('receipt')).toHaveText(/RC-[0-9A-F]{12}/);
  // $300.00 plus a $2.00 fee (50 cents + 0.5%) leaves $698.00.
  await expect(remaining).toHaveText('$698.00');

  // Nothing is kept in the browser: a fresh load reads the same state from the API.
  // Nothing sensitive is stored either: the refresh cookie is HttpOnly, so a reload signs back in through it.
  expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).toBe(
    '{}',
  );
  await page.reload();
  await expect(page.getByTestId('whoami')).toContainText(ADMIN.email);
  await page.getByRole('button', { name: new RegExp(`^${code}`) }).click();
  const reloaded = page.locator(`tr[data-reference="${reference}"]`);
  await expect(reloaded.getByTestId('status')).toHaveText('Settled');
  await expect(page.getByTestId('selected-remaining')).toHaveText('$698.00');

  await reloaded.getByRole('button', { name: 'Reverse' }).click();
  await expect(reloaded.getByTestId('status')).toHaveText('Reversed');
  await expect(page.getByTestId('selected-remaining')).toHaveText('$1,000.00');
  // The issued receipt is not rolled back.
  await expect(reloaded.getByTestId('receipt')).toHaveText(/RC-[0-9A-F]{12}/);
  const headers = { Authorization: `Bearer ${await adminToken(page.request)}` };
  const payments = await page.request.get(`/api/payments?accountCode=${code}`, { headers });
  const [payment] = await payments.json();
  const receipt = await page.request.get(`/api/receipts/${payment.id}`, { headers });
  expect((await receipt.json()).status).toBe('Issued');
});
