import type { RequestOptions } from '../api/client';
export type OperationKind = 'checkout' | 'payment';
const namespace = (user: string, kind: OperationKind, resource: string) =>
  `ecommerce:operation:v1:${encodeURIComponent(user)}:${kind}:${encodeURIComponent(resource)}`;
export const OperationKeys = {
  getOrCreate(user: string, kind: OperationKind, resource: string): string {
    const name = namespace(user, kind, resource);
    const existing = sessionStorage.getItem(name);
    if (existing) return existing;
    const key = crypto.randomUUID();
    sessionStorage.setItem(name, key);
    return key;
  },
  confirm(user: string, kind: OperationKind, resource: string) {
    sessionStorage.removeItem(namespace(user, kind, resource));
  },
  has(user: string, kind: OperationKind, resource: string) {
    return sessionStorage.getItem(namespace(user, kind, resource)) !== null;
  },
};
export class PaymentUncertain extends Error {
  constructor(
    public orderId: string,
    public cause: unknown,
  ) {
    super(
      'Order created. Payment creation is uncertain; open the order to recover or retry its payment.',
    );
  }
}
export async function ensurePayment<T>(
  user: string,
  order: string,
  request: <R>(path: string, options?: RequestOptions) => Promise<R>,
): Promise<T> {
  const key = OperationKeys.getOrCreate(user, 'payment', order);
  const payment = await request<T>(`/orders/${order}/payments`, { method: 'POST', key });
  OperationKeys.confirm(user, 'payment', order);
  return payment;
}
export async function checkoutAndPayment(
  user: string,
  request: <R>(path: string, options?: RequestOptions) => Promise<R>,
): Promise<{ id: string }> {
  const key = OperationKeys.getOrCreate(user, 'checkout', 'cart');
  const order = await request<{ id: string }>('/cart/checkout', { method: 'POST', key });
  OperationKeys.confirm(user, 'checkout', 'cart');
  try {
    await ensurePayment(user, order.id, request);
  } catch (e) {
    if (e instanceof Error && e.name === 'AbortError') throw e;
    throw new PaymentUncertain(order.id, e);
  }
  return order;
}
