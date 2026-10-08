import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: 'e2e',
  workers: 1,
  timeout: 90000,
  use: {
    baseURL: 'http://127.0.0.1:5088',
    browserName: 'chromium',
    screenshot: 'only-on-failure',
    trace: 'off',
  },
  reporter: 'list',
});
