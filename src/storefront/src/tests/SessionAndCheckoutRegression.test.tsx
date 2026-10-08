import React from 'react';
import { act, render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { AuthProvider, useAuth } from '../auth/AuthProvider';
import { CartPage } from '../features/cart/CartPage';
import { OperationKeys } from '../lib/operationKeys';
let auth: ReturnType<typeof useAuth>;
function Probe() {
  auth = useAuth();
  return <span>{auth.session?.user.userId ?? 'anonymous'}</span>;
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
});
it('late registration must not replace B login', async () => {
  let finishRegister!: (r: Response) => void;
  vi.stubGlobal(
    'fetch',
    vi.fn((path: string, options: any) => {
      if (path === '/auth/register') return new Promise<Response>((r) => (finishRegister = r));
      if (path === '/auth/login')
        return Promise.resolve(
          new Response(JSON.stringify({ accessToken: JSON.parse(options.body).email })),
        );
      if (path === '/auth/me')
        return Promise.resolve(
          new Response(
            JSON.stringify({
              userId: new Headers(options.headers).get('Authorization')!.slice(7),
              permissions: [],
            }),
          ),
        );
      throw Error(path);
    }),
  );
  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  );
  const pending = auth.register('A', 'password');
  await act(() => auth.login('B', 'password'));
  expect(auth.session?.user.userId).toBe('B');
  await act(async () => {
    finishRegister(new Response('{}'));
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });
  expect(auth.session?.user.userId).toBe('B');
});
it('previous checkout recovery must be available during Redis outage', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn((path: string) => {
      if (path === '/auth/login') return Promise.resolve(new Response('{"accessToken":"opaque"}'));
      if (path === '/auth/me')
        return Promise.resolve(new Response('{"userId":"A","permissions":[]}'));
      if (path === '/cart')
        return Promise.resolve(new Response('{"error":"Cart unavailable"}', { status: 503 }));
      throw Error(path);
    }),
  );
  OperationKeys.getOrCreate('A', 'checkout', 'cart');
  render(
    <MemoryRouter>
      <AuthProvider>
        <Probe />
        <CartPage />
      </AuthProvider>
    </MemoryRouter>,
  );
  await act(() => auth.login('A', 'password'));
  await screen.findByRole('alert');
  const retry = screen.queryByRole('button', { name: 'Retry previous checkout' });
  expect(retry).not.toBeNull();
  expect(retry?.hasAttribute('disabled')).toBe(false);
});
it('previous checkout recovery must ignore an unsupported current cart price', async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn((path: string) => {
      if (path === '/auth/login') return Promise.resolve(new Response('{"accessToken":"opaque"}'));
      if (path === '/auth/me')
        return Promise.resolve(new Response('{"userId":"A","permissions":[]}'));
      if (path === '/cart')
        return Promise.resolve(new Response('{"items":[{"productId":1,"quantity":2}]}'));
      if (path === '/products/1')
        return Promise.resolve(
          new Response(
            '{"id":1,"name":"edited","category":"test","description":"","priceCents":9007199254740992,"currency":"EUR","available":10}',
          ),
        );
      throw Error(path);
    }),
  );
  OperationKeys.getOrCreate('A', 'checkout', 'cart');
  render(
    <MemoryRouter>
      <AuthProvider>
        <Probe />
        <CartPage />
      </AuthProvider>
    </MemoryRouter>,
  );
  await act(() => auth.login('A', 'password'));
  const retry = await screen.findByRole('button', { name: 'Retry previous checkout' });
  expect(retry.hasAttribute('disabled')).toBe(false);
});
