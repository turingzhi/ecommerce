import { test, expect } from '@playwright/test';
import { execFileSync } from 'node:child_process';

function setting(name: string): string {
  try {
    return execFileSync('docker', ['compose', 'exec', '-T', 'ecommerce', 'printenv', name], {
      cwd: '../..',
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe'],
    }).replace(/[\r\n]+$/, '');
  } catch {
    return '';
  }
}

test('configured default admin signs in and opens all current admin pages', async ({ page }) => {
  test.skip(
    setting('DefaultAdmin__Enabled').toLowerCase() !== 'true',
    'Optional default admin setup is disabled',
  );
  const email = setting('DefaultAdmin__Email').trim();
  const password = process.env.ECOMMERCE_TEST_ADMIN_PASSWORD || setting('DefaultAdmin__Password');
  test.skip(
    !password,
    'Bootstrap password removed; provide existing password through ECOMMERCE_TEST_ADMIN_PASSWORD',
  );
  await page.goto('/#/login');
  await expect(page.getByLabel('Email', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Password', { exact: true })).toBeVisible();
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
  for (const [label, heading] of [
    ['Products admin', 'Catalog administration'],
    ['Orders admin', 'Orders administration'],
    ['Payments admin', 'Payments administration'],
  ]) {
    await page.getByRole('link', { name: label, exact: true }).click();
    await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible();
  }
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('link', { name: 'Products admin', exact: true })).toHaveCount(0);
});
