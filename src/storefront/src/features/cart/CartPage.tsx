import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import type { Cart, Details } from '../../api/types';
import { apiRequest, ApiError } from '../../api/client';
import { ErrorNotice } from '../../components/ErrorNotice';
import { formatMoney, totalCents } from '../../lib/money';
import { checkoutAndPayment, OperationKeys, PaymentUncertain } from '../../lib/operationKeys';
export async function hydrateCart(cart: Cart, signal: AbortSignal) {
  const products = new Map<number, Details | null>();
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(5, cart.items.length) }, async () => {
      while (next < cart.items.length) {
        const item = cart.items[next++];
        try {
          const product = await apiRequest<Details>(`/products/${item.productId}`, { signal });
          products.set(item.productId, product);
        } catch (e) {
          if (e instanceof ApiError && e.status === 404) products.set(item.productId, null);
          else throw e;
        }
      }
    }),
  );
  return { cart, products };
}
export function CartPage() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [revision, setRevision] = useState(0),
    [busy, setBusy] = useState(false),
    [error, setError] = useState<unknown>(null);
  const state = usePrivateRequest(
    async (request, signal) => hydrateCart(await request<Cart>('/cart', { signal }), signal),
    [revision],
  );
  if (!auth.session)
    return (
      <p>
        <Link to="/login">Sign in</Link> to view your cart.
      </p>
    );
  const mutate = async (path: string, method: string, body?: unknown) => {
    setBusy(true);
    setError(null);
    try {
      await auth.request(path, { method, body });
      setRevision((x) => x + 1);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  let total: string | null = null;
  let priceError: unknown = null;
  try {
    if (state.data && state.data.cart.items.some((i) => !state.data!.products.get(i.productId)))
      throw Error('A cart product is unavailable. Remove it before checkout.');
    if (state.data)
      total = formatMoney(
        totalCents(
          state.data.cart.items.map((i) => ({
            quantity: i.quantity,
            unitPriceCents: state.data!.products.get(i.productId)!.priceCents,
          })),
        ),
      );
  } catch (e) {
    priceError = e;
  }
  const checkout = async () => {
    setBusy(true);
    setError(null);
    try {
      const order = await checkoutAndPayment(auth.session!.user.userId, auth.request);
      navigate(`/orders/${order.id}`);
    } catch (e) {
      if (e instanceof PaymentUncertain)
        navigate(`/orders/${e.orderId}`, { state: { paymentUncertain: true } });
      else setError(e);
    } finally {
      setBusy(false);
    }
  };
  const uncertain = OperationKeys.has(auth.session.user.userId, 'checkout', 'cart');
  return (
    <>
      <h1>Your cart</h1>
      <p>Checkout uses current SQL prices and stock. Your cart stays here after checkout.</p>
      <ErrorNotice error={state.error || error || priceError} />
      {uncertain && (
        <section className="panel">
          <p className="notice">
            An unresolved checkout key will replay its original order, even if the cart has changed.
            Recovery does not require reading the current cart.
          </p>
          <button disabled={busy} onClick={() => void checkout()}>
            {busy ? 'Processing...' : 'Retry previous checkout'}
          </button>
        </section>
      )}
      {state.loading && <p>Loading cart…</p>}
      {state.data && (
        <>
          <div className="panel">
            {state.data.cart.items.map((item) => {
              const p = state.data!.products.get(item.productId);
              if (!p)
                return (
                  <div className="cart-row" key={item.productId}>
                    <p>Product #{item.productId} is unavailable.</p>
                    <button
                      className="secondary"
                      disabled={busy}
                      onClick={() => void mutate(`/cart/items/${item.productId}`, 'DELETE')}
                    >
                      Remove
                    </button>
                  </div>
                );
              return (
                <div className="cart-row" key={item.productId}>
                  <div>
                    <Link to={`/products/${item.productId}`}>{p.name}</Link>
                    <p>
                      {Number.isSafeInteger(p.priceCents) && p.priceCents >= 0
                        ? formatMoney(p.priceCents)
                        : 'Unsupported price range'}{' '}
                      each · {p.available} available
                    </p>
                  </div>
                  <label>
                    Quantity
                    <input
                      aria-label={`Quantity for ${p.name}`}
                      type="number"
                      min={1}
                      max={100}
                      defaultValue={item.quantity}
                      key={`${revision}:${item.productId}`}
                      disabled={busy}
                      onBlur={(e) => {
                        const quantity = Number(e.target.value);
                        if (
                          Number.isInteger(quantity) &&
                          quantity >= 1 &&
                          quantity <= 100 &&
                          quantity !== item.quantity
                        )
                          void mutate(`/cart/items/${item.productId}`, 'PUT', { quantity });
                      }}
                    />
                  </label>
                  <button
                    className="secondary"
                    disabled={busy}
                    onClick={() => void mutate(`/cart/items/${item.productId}`, 'DELETE')}
                  >
                    Remove
                  </button>
                </div>
              );
            })}
            {!state.data.cart.items.length && <p>Your cart is empty.</p>}
          </div>
          <div className="checkout-bar">
            <strong>Total estimate: {total ?? 'Unavailable'}</strong>
            {!uncertain && (
              <button
                disabled={busy || !!priceError || !state.data.cart.items.length}
                onClick={() => void checkout()}
              >
                {busy ? 'Processing...' : 'Checkout'}
              </button>
            )}
            <button
              className="secondary"
              disabled={busy || !state.data.cart.items.length}
              onClick={() => void mutate('/cart', 'DELETE')}
            >
              Clear cart
            </button>
          </div>
          <p className="muted">
            Default checkout creates a Pending payment. No real payment provider is connected.
          </p>
        </>
      )}
    </>
  );
}
