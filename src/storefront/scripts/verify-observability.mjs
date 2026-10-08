import { chromium } from 'playwright';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const cwd = fileURLToPath(new URL('../../../', import.meta.url));
const compose = (...args) =>
  execFileSync('docker', ['compose', ...args], { cwd, stdio: 'pipe', encoding: 'utf8' });
const overlay = ['-f', 'compose.yaml', '-f', 'compose.observability.yaml'];
let browser;
try {
  compose(...overlay, 'up', '-d', '--wait');
  const base = 'http://127.0.0.1:5088';
  for (let i = 0; i < 2; i++) {
    const r = await fetch(base + '/products/search?q=wireless');
    if (!r.ok) throw Error('Search telemetry fixture failed');
  }
  const logs = compose(...overlay, 'logs', 'dashboard');
  const match = Array.from(logs.matchAll(/https?:\/\/[^\s]+\/login\?t=[^\s]+/g)).at(-1);
  if (!match) throw Error('Viewer login URL missing');
  browser = await chromium.launch();
  const page = await browser.newPage();
  await page.goto(match[0].replace('localhost', '127.0.0.1'));
  await page.getByRole('link', { name: 'Traces', exact: true }).click();
  await page.getByRole('row').nth(1).waitFor({ timeout: 30000 });
  console.log('PASS: exported request/dependency traces visible');
  await page.getByRole('link', { name: 'Metrics', exact: true }).click();
  await page
    .getByText('ecommerce.search.cache.requests', { exact: true })
    .waitFor({ timeout: 75000 });
  console.log('PASS: commerce cache meter exported');
  compose(...overlay, 'stop', 'dashboard');
  const healthy = await fetch(base + '/health');
  if (!healthy.ok) throw Error('Viewer outage changed API readiness');
  const search = await fetch(base + '/products/search?q=wireless');
  if (!search.ok) throw Error('Viewer outage changed search');
  console.log('PASS: viewer outage leaves readiness/search available');
} catch (error) {
  console.error(
    'Observability smoke failed:',
    String(error?.message ?? error)
      .split('\n')[0]
      .replace(/https?:\/\/[^\s]+/g, '[URL withheld]'),
  );
  process.exitCode = 1;
} finally {
  await browser?.close();
  compose('up', '-d', '--wait', 'ecommerce');
  compose(...overlay, 'stop', 'dashboard');
  compose(...overlay, 'rm', '-f', 'dashboard');
}
