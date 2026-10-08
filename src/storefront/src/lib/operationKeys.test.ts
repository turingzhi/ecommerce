import { beforeEach, expect, it, vi } from 'vitest';
import { OperationKeys, checkoutAndPayment } from './operationKeys';
beforeEach(() => sessionStorage.clear());
it('preserves uncertain checkout across edits and refresh, separates customers and resources', () => {
  const key = OperationKeys.getOrCreate('A', 'checkout', 'cart');
  expect(OperationKeys.getOrCreate('A', 'checkout', 'cart')).toBe(key);
  expect(OperationKeys.getOrCreate('B', 'checkout', 'cart')).not.toBe(key);
  expect(OperationKeys.getOrCreate('A', 'payment', 'order')).not.toBe(key);
  OperationKeys.confirm('A', 'checkout', 'cart');
  expect(OperationKeys.getOrCreate('A', 'checkout', 'cart')).not.toBe(key);
});
it('retries a committed uncertain order using its original key and never clears cart', async () => {
  const request = vi
    .fn()
    .mockRejectedValueOnce(Error('network'))
    .mockResolvedValueOnce({ id: 'original-order' })
    .mockResolvedValueOnce({ id: 'payment' });
  await expect(checkoutAndPayment('A', request)).rejects.toThrow('network');
  const original = request.mock.calls[0][1].key;
  expect(OperationKeys.getOrCreate('A', 'checkout', 'cart')).toBe(original);
  await checkoutAndPayment('A', request);
  expect(request.mock.calls[1][1].key).toBe(original);
  expect(request.mock.calls[2][0]).toBe('/orders/original-order/payments');
  expect(request.mock.calls.some((c) => c[1].method === 'DELETE')).toBe(false);
});
it('preserves checkout resolution when payment is uncertain', async () => {
  const request = vi
    .fn()
    .mockResolvedValueOnce({ id: 'known-order' })
    .mockRejectedValueOnce(Error('payment uncertain'));
  await expect(checkoutAndPayment('A', request)).rejects.toMatchObject({ orderId: 'known-order' });
  expect(sessionStorage.length).toBe(1);
  expect(sessionStorage.key(0)).toContain(':payment:known-order');
});
