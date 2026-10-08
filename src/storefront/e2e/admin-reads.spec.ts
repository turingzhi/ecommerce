import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { execFileSync } from 'node:child_process';

const password = 'AdminBrowser!123456';
async function account(api: APIRequestContext, reader = false) {
  const email = `admin-browser-${crypto.randomUUID()}@example.invalid`;
  expect((await api.post('/auth/register', { data: { email, password } })).status()).toBe(200);
  if (reader)
    for (const flag of ['--grant-order-reader', '--grant-payment-reader'])
      execFileSync(
        'docker',
        ['compose', 'exec', '-T', 'ecommerce', 'dotnet', 'Ecommerce.Api.dll', flag, email],
        { cwd: '../..', stdio: 'pipe' },
      );
  const response = await api.post('/auth/login', { data: { email, password } });
  expect(response.status()).toBe(200);
  return { email, token: (await response.json()).accessToken };
}
async function signIn(page: Page, email: string) {
  await page.goto('/#/login');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
}

test('operator views another customer order, follows its payments and uses filters on mobile', async ({
  page,
  request,
}, testInfo) => {
  const operator = await account(request, true),
    customer = await account(request);
  const output = execFileSync(
    'docker',
    [
      'compose',
      'exec',
      '-T',
      'ecommerce',
      'dotnet',
      'Ecommerce.Api.dll',
      '--create-product',
      `Admin browser ${crypto.randomUUID()}`,
      'Admin browser fixture',
      'Verification',
      '1234',
      '4',
    ],
    { cwd: '../..', encoding: 'utf8' },
  );
  const productId = Number(/Saved product (\d+) at version/.exec(output)![1]);
  const headers = { Authorization: `Bearer ${customer.token}` };
  const orderResponse = await request.post('/orders', {
    headers: { ...headers, 'Idempotency-Key': crypto.randomUUID() },
    data: { items: [{ productId, quantity: 2 }] },
  });
  expect(orderResponse.status()).toBe(201);
  const order = await orderResponse.json();
  const paymentResponse = await request.post(`/orders/${order.id}/payments`, {
    headers: { ...headers, 'Idempotency-Key': crypto.randomUUID() },
  });
  expect(paymentResponse.status()).toBe(201);
  const payment = await paymentResponse.json();
  await signIn(page, operator.email);
  await page.getByRole('link', { name: 'Orders admin', exact: true }).click();
  await page.getByRole('link', { name: order.id, exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Order details', exact: true })).toBeVisible();
  await expect(page.getByText('Quantity: 2')).toBeVisible();
  await expect(page.getByText('Unit price: EUR 12.34')).toBeVisible();
  await page.getByRole('link', { name: 'View payments for this order' }).click();
  await expect(page.locator('.admin-record')).toHaveCount(1);
  await page.getByRole('link', { name: payment.id, exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Payment details', exact: true })).toBeVisible();
  await expect(page.getByText('EUR 24.68', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Back to all payments' }).click();
  await page.getByRole('combobox', { name: 'Payment status', exact: true }).selectOption('Pending');
  await expect(page.locator('.admin-record').first()).toBeVisible();
  await expect
    .poll(async () => {
      const statuses = await page.locator('.admin-record .badge').allTextContents();
      return statuses.length > 0 && statuses.every((status) => status === 'Pending');
    })
    .toBe(true);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath('ecommerce-admin-payments-mobile.png'),
    fullPage: true,
  });
  await page.getByRole('link', { name: 'Orders admin', exact: true }).click();
  await page
    .getByRole('combobox', { name: 'Order status', exact: true })
    .selectOption('PendingPayment');
  await expect(page.locator('.admin-record').first()).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.goto('/#/admin/payments');
  await expect(
    page.getByText('Sign in with payments:read permission to use this page.'),
  ).toBeVisible();
  expect(
    (
      await request.get(`/payments/${payment.id}`, {
        headers: { Authorization: `Bearer ${operator.token}` },
      })
    ).status(),
  ).toBe(404);
});

test('customer cannot open admin lists or details', async ({ page, request }) => {
  const customer = await account(request);
  await signIn(page, customer.email);
  await expect(page.getByRole('link', { name: 'Orders admin', exact: true })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Payments admin', exact: true })).toHaveCount(0);
  for (const resource of ['orders', 'payments'])
    for (const suffix of ['', `/${crypto.randomUUID()}`]) {
      await page.goto(`/#/admin/${resource}${suffix}`);
      await expect(
        page.getByText(`Sign in with ${resource}:read permission to use this page.`),
      ).toBeVisible();
      expect(
        (
          await request.get(`/admin/${resource}${suffix}`, {
            headers: { Authorization: `Bearer ${customer.token}` },
          })
        ).status(),
      ).toBe(403);
    }
});
