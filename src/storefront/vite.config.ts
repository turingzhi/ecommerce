import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
const proxy = Object.fromEntries(
  ['/auth', '/products', '/cart', '/orders', '/payments', '/refunds', '/admin', '/ui', '/dev'].map(
    (path) => [path, { target: 'http://127.0.0.1:5088', changeOrigin: true }],
  ),
);
export default defineConfig({
  plugins: [react()],
  server: { host: '127.0.0.1', port: 5173, strictPort: true, proxy },
  test: { environment: 'jsdom', include: ['src/**/*.test.{ts,tsx}'], restoreMocks: true },
});
