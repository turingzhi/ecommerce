import React from 'react';
import { act, render, screen, cleanup } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { AuthProvider, useAuth } from './AuthProvider';
import { ApiError } from '../api/client';
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  sessionStorage.clear();
  window.localStorage.clear();
});
let auth: ReturnType<typeof useAuth>;
function Probe() {
  auth = useAuth();
  return <span>{auth.session?.user.userId ?? 'anonymous'}</span>;
}
it('holds opaque token only in memory and rejects private completion after account change', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response('{"accessToken":"opaque"}'))
      .mockResolvedValueOnce(new Response('{"userId":"A","email":"a","permissions":[]}'))
      .mockResolvedValueOnce(new Response('{"accessToken":"second"}'))
      .mockResolvedValueOnce(new Response('{"userId":"B","email":"b","permissions":[]}')),
  );
  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  );
  await act(() => auth.login('a', 'password'));
  expect(screen.getByText('A')).toBeTruthy();
  let resolve!: (value: unknown) => void;
  vi.stubGlobal(
    'fetch',
    vi.fn(
      () =>
        new Promise((r) => {
          resolve = r;
        }),
    ),
  );
  const pending = auth.request('/cart');
  act(() => auth.logout());
  resolve(new Response('{"items":[{"productId":99,"quantity":1}]}'));
  await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  expect(sessionStorage.length + window.localStorage.length).toBe(0);
});
it('clears session on 401 and keeps session on 403', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response('{"accessToken":"opaque"}'))
      .mockResolvedValueOnce(new Response('{"userId":"A","permissions":[]}'))
      .mockResolvedValueOnce(new Response('Denied', { status: 403 }))
      .mockResolvedValueOnce(new Response('Expired', { status: 401 })),
  );
  render(
    <AuthProvider>
      <Probe />
    </AuthProvider>,
  );
  await act(() => auth.login('a', 'password'));
  await act(async () => {
    await expect(auth.request('/admin/products')).rejects.toBeInstanceOf(ApiError);
  });
  expect(auth.session?.user.userId).toBe('A');
  await act(async () => {
    await expect(auth.request('/cart')).rejects.toBeInstanceOf(ApiError);
  });
  expect(auth.session).toBeNull();
});
