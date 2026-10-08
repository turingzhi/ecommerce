import { expect, it, vi, afterEach } from 'vitest';
import { hydrateCart } from './CartPage';
afterEach(() => vi.unstubAllGlobals());
it('keeps missing products removable while blocking checkout', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 404 })));
  const state = await hydrateCart(
    { items: [{ productId: 99, quantity: 1 }] },
    new AbortController().signal,
  );
  expect(state.cart.items).toHaveLength(1);
  expect(state.products.get(99)).toBeNull();
});
