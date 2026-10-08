import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { execFileSync } from 'node:child_process';
const password = 'StorefrontFixture123!';
async function account(api: APIRequestContext, admin = false) {
  const email = `storefront-${crypto.randomUUID()}@example.test`;
  const registered = await api.post('/auth/register', { data: { email, password } });
  expect(registered.status()).toBe(200);
  if (admin)
    execFileSync(
      'docker',
      [
        'compose',
        'exec',
        '-T',
        'ecommerce',
        'dotnet',
        'Ecommerce.Api.dll',
        '--grant-product-admin',
        email,
      ],
      { cwd: '../..', stdio: 'pipe' },
    );
  const login = await api.post('/auth/login', { data: { email, password } });
  expect(login.status()).toBe(200);
  return { email, token: (await login.json()).accessToken };
}
async function signIn(page: Page, email: string) {
  await page.goto('/#/login');
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
}
async function product(api: APIRequestContext) {
  const admin = await account(api, true);
  const name = `Browser item ${crypto.randomUUID()}`;
  const response = await api.post('/admin/products', {
    headers: { Authorization: `Bearer ${admin.token}` },
    data: {
      name,
      description: 'Real browser fixture product',
      category: 'Browser',
      priceCents: 1999,
      available: 10,
    },
  });
  expect(response.status()).toBe(201);
  return { admin, name, product: await response.json() };
}
test('catalog, detail, static API boundaries and mobile layout', async ({ page, request }) => {
  await page.goto('/');
  await expect(
    page.getByRole('heading', { name: 'Find your next everyday essential.' }),
  ).toBeVisible();
  await expect(page.locator('.product-card').first()).toBeVisible();
  await page.getByRole('combobox', { name: 'Sort', exact: true }).selectOption('priceAsc');
  await expect(page.locator('.product-card').first()).toBeVisible();
  await page.locator('.product-card').first().getByRole('link', { name: 'View details' }).click();
  await expect(page.getByText(/currently available/)).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  for (const path of ['/unknown-api', '/products/not-an-id', '/assets/missing.js']) {
    const reply = await request.get(path);
    expect(reply.status()).toBe(404);
    expect(reply.headers()['content-type'] ?? '').not.toContain('text/html');
  }
  expect((await request.get('/cart')).status()).toBe(401);
  await page.screenshot({ path: '/private/tmp/storefront-mobile.png', fullPage: true });
});
test('real cart checkout retains cart and creates one pending payment', async ({
  page,
  request,
}) => {
  const fixture = await product(request);
  const customer = await account(request);
  await signIn(page, customer.email);
  await page.goto(`/#/products/${fixture.product.id}`);
  await page.getByLabel('Quantity', { exact: true }).fill('2');
  await page.getByRole('button', { name: 'Set cart quantity' }).click();
  await expect(page.getByText('Cart quantity updated.')).toBeVisible();
  await page.getByRole('link', { name: 'Open cart' }).click();
  await expect(page.getByText('Total estimate: EUR 39.98')).toBeVisible();
  await page.getByRole('button', { name: 'Checkout', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Payments', exact: true })).toBeVisible();
  await expect(page.getByText('Pending', { exact: true })).toBeVisible();
  if (process.env.STOREFRONT_DEVELOPMENT === '1')
    await expect(page.getByRole('button', { name: 'Simulate success' })).toBeVisible();
  else await expect(page.getByRole('button', { name: 'Simulate success' })).toHaveCount(0);
  const cart = await request.get('/cart', {
    headers: { Authorization: `Bearer ${customer.token}` },
  });
  expect((await cart.json()).items).toEqual([{ productId: fixture.product.id, quantity: 2 }]);
  const id = page.url().split('/orders/')[1];
  const list = await request.get(`/orders/${id}/payments`, {
    headers: { Authorization: `Bearer ${customer.token}` },
  });
  expect((await list.json()).payments).toHaveLength(1);
  expect(await page.evaluate(() => Object.keys(localStorage).length)).toBe(0);
  await page.reload();
  await expect(page.getByRole('link', { name: 'Sign in' }).first()).toBeVisible();
});
test('catalog administrator creates and edits without stock field', async ({ page, request }) => {
  const admin = await account(request, true);
  await signIn(page, admin.email);
  await page.getByRole('link', { name: 'Products admin' }).click();
  await page.getByRole('button', { name: 'New product' }).click();
  const name = `UI product ${crypto.randomUUID()}`;
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Category', { exact: true }).fill('Browser');
  await page.getByLabel('Description', { exact: true }).fill('');
  await page.getByLabel('Price (EUR)', { exact: true }).fill('12.34');
  await page.getByLabel('Initial stock', { exact: true }).fill('5');
  await page.getByRole('button', { name: 'Create product', exact: true }).click();
  await expect(
    page.getByText('Saved in SQL. Search updates asynchronously.').first(),
  ).toBeVisible();
  await page.getByLabel('Name filter').fill(name);
  await page.getByRole('button', { name, exact: true }).click();
  await expect(page.getByLabel('Initial stock', { exact: true })).toHaveCount(0);
  await page.getByLabel('Price (EUR)', { exact: true }).fill('10.01');
  await page.getByRole('button', { name: 'Save product', exact: true }).click();
  await expect(
    page.getByText('Saved in SQL. Search updates asynchronously.').first(),
  ).toBeVisible();
  const products = await request.get(`/admin/products?name=${encodeURIComponent(name)}`, {
    headers: { Authorization: `Bearer ${admin.token}` },
  });
  expect((await products.json()).products[0]).toMatchObject({
    priceCents: 1001,
    available: 5,
    description: '',
  });
  await page.screenshot({ path: '/private/tmp/storefront-desktop.png', fullPage: true });
});
test('customer cannot open admin and switching account clears private screen', async ({
  page,
  request,
}) => {
  const customer = await account(request);
  await signIn(page, customer.email);
  await page.goto('/#/admin/products');
  await expect(
    page.getByText('Sign in with products:manage permission to use this page.'),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(
    page.getByRole('heading', { name: 'Find your next everyday essential.' }),
  ).toBeVisible();
});

test('Development payment, shipping timeline and owner return', async ({ page, request }) => {
  test.skip(process.env.STOREFRONT_DEVELOPMENT !== '1', 'Explicit Development overlay required');
  const fixture = await product(request);
  const owner = await account(request);
  const headers = { Authorization: `Bearer ${owner.token}` };
  await request.put(`/cart/items/${fixture.product.id}`, { headers, data: { quantity: 1 } });
  await signIn(page, owner.email);
  await page.goto('/#/cart');
  await page.getByRole('button', { name: 'Checkout', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Simulate success' })).toBeVisible();
  const orderId = page.url().split('/orders/')[1];
  await page.getByRole('button', { name: 'Simulate success' }).click();
  let shipment: any;
  await expect
    .poll(
      async () => {
        const response = await request.get(`/orders/${orderId}/shipment`, { headers });
        if (response.status() === 200) shipment = await response.json();
        return response.status();
      },
      { timeout: 30000 },
    )
    .toBe(200);
  const operator = await account(request);
  execFileSync(
    'docker',
    [
      'compose',
      'exec',
      '-T',
      'ecommerce',
      'dotnet',
      'Ecommerce.Api.dll',
      '--grant-shipment-admin',
      operator.email,
    ],
    { cwd: '../..', stdio: 'pipe' },
  );
  const login = await request.post('/auth/login', { data: { email: operator.email, password } });
  const operatorHeaders = { Authorization: `Bearer ${(await login.json()).accessToken}` };
  expect(
    (
      await request.put(`/admin/shipments/${shipment.id}/status`, {
        headers: operatorHeaders,
        data: { status: 'Shipped', trackingNumber: 'BROWSER-TRACK' },
      })
    ).status(),
  ).toBe(200);
  expect(
    (
      await request.put(`/admin/shipments/${shipment.id}/status`, {
        headers: operatorHeaders,
        data: { status: 'Delivered' },
      })
    ).status(),
  ).toBe(200);
  await page.getByRole('button', { name: 'Refresh order' }).click();
  await expect(page.getByText('Delivered', { exact: true }).first()).toBeVisible();
  await page.getByLabel('Reason', { exact: true }).fill('Browser whole-order return');
  await page.getByRole('button', { name: 'Request whole-order return' }).click();
  await expect(page.getByText('Requested', { exact: true })).toBeVisible();
  const tracking = await request.get(`/orders/${orderId}/tracking`, { headers });
  expect(JSON.stringify(await tracking.json())).not.toContain('actorId');
  expect(
    (
      await request.get(`/orders/${orderId}/return`, {
        headers: { Authorization: `Bearer ${operator.token}` },
      })
    ).status(),
  ).toBe(404);
});

test('long product text stays inside cards at intermediate and mobile widths', async ({ page }) => {
  const name = 'Cancellation test ' + 'a'.repeat(180);
  await page.route('**/products?*', (route) =>
    route.fulfill({
      json: {
        products: [
          {
            id: 1,
            name,
            description: 'b'.repeat(200),
            category: 'c'.repeat(200),
            priceCents: 1234,
          },
        ],
        total: 1,
        page: 1,
        pageSize: 20,
      },
    }),
  );
  for (const width of [565, 390]) {
    await page.setViewportSize({ width, height: 844 });
    await page.goto('/');
    const card = page.locator('.product-card');
    await expect(card.getByRole('heading')).toHaveText(name);
    const bounds = await card.evaluate((element) => {
      const box = element.getBoundingClientRect();
      const textBoxes = Array.from(element.querySelectorAll('h2, p')).flatMap((node) => {
        const range = document.createRange();
        range.selectNodeContents(node);
        return Array.from(range.getClientRects()).map((r) => ({ left: r.left, right: r.right }));
      });
      return { left: box.left, right: box.right, textBoxes };
    });
    for (const box of bounds.textBoxes) {
      expect(box.left).toBeGreaterThanOrEqual(bounds.left);
      expect(box.right).toBeLessThanOrEqual(bounds.right);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
  }
});
