import { afterEach, expect, it, vi } from 'vitest';
import { apiRequest, ApiError } from './client';
afterEach(() => vi.unstubAllGlobals());
it('sends JSON, bearer and repeat-safe key, parses JSON', async () => {
  const fetch = vi.fn().mockResolvedValue(new Response('{"id":1}', { status: 201 }));
  vi.stubGlobal('fetch', fetch);
  expect(
    await apiRequest('/orders', {
      method: 'POST',
      body: { items: [] },
      token: 'opaque',
      key: 'stable',
    }),
  ).toEqual({ id: 1 });
  const headers = new Headers(fetch.mock.calls[0][1].headers);
  expect(headers.get('Authorization')).toBe('Bearer opaque');
  expect(headers.get('Idempotency-Key')).toBe('stable');
});
it('handles empty replies and Retry-After', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(
        new Response('{"error":"Wait"}', { status: 429, headers: { 'Retry-After': '60' } }),
      ),
  );
  expect(await apiRequest('/cart')).toBeUndefined();
  try {
    await apiRequest('/cart');
    throw Error('expected error');
  } catch (e) {
    expect(e).toBeInstanceOf(ApiError);
    expect((e as ApiError).retryAfterSeconds).toBe(60);
  }
});
it('keeps validation and plaintext errors readable', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValueOnce(new Response('{"errors":{"Password":["Too short"]}}', { status: 400 }))
      .mockResolvedValueOnce(new Response('Unavailable', { status: 503 })),
  );
  await expect(apiRequest('/auth/register')).rejects.toMatchObject({
    details: { Password: ['Too short'] },
  });
  await expect(apiRequest('/cart')).rejects.toThrow('Unavailable');
});
it('forwards cancellation without swallowing it', async () => {
  const controller = new AbortController();
  controller.abort();
  const fetch = vi.fn().mockRejectedValue(new DOMException('Aborted', 'AbortError'));
  vi.stubGlobal('fetch', fetch);
  await expect(apiRequest('/products', { signal: controller.signal })).rejects.toMatchObject({
    name: 'AbortError',
  });
  expect(fetch.mock.calls[0][1].signal).toBe(controller.signal);
});
