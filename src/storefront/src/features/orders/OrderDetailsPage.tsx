import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { apiRequest } from '../../api/client';
import type { Order, Payment, Refund } from '../../api/types';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import { useLatestRequest } from '../../hooks/useLatestRequest';
import { ErrorNotice } from '../../components/ErrorNotice';
import { formatMoney, totalCents } from '../../lib/money';
import { ensurePayment, OperationKeys } from '../../lib/operationKeys';
export function OrderItems({ order }: { order: Order }) {
  try {
    return (
      <>
        <h2>Purchased items</h2>
        {order.orderItems.map((item) => (
          <div className="order-row" key={item.id}>
            <Link to={`/products/${item.productId}`}>Product #{item.productId}</Link>
            <span>
              {item.quantity} × <span>{formatMoney(item.unitPriceCents, order.currency)}</span>
            </span>
          </div>
        ))}
        <p>
          Total: <strong>{formatMoney(totalCents(order.orderItems), order.currency)}</strong>
        </p>
      </>
    );
  } catch (e) {
    return <ErrorNotice error={e} />;
  }
}
export function OrderDetailsPage() {
  const { id } = useParams();
  const auth = useAuth();
  const [revision, setRevision] = useState(0),
    [busy, setBusy] = useState(false),
    [error, setError] = useState<unknown>(null);
  const config = useLatestRequest(
    (signal) => apiRequest<{ paymentSimulationEnabled: boolean }>('/ui/config', { signal }),
    [],
  );
  const state = usePrivateRequest(
    async (request, signal) => {
      const order = await request<Order>(`/orders/${id}`, { signal });
      const paymentList = await request<{ payments: Payment[] }>(
        `/orders/${id}/payments?pageSize=50`,
        { signal },
      );
      const refunds = await Promise.all(
        paymentList.payments.map(async (p) => ({
          payment: p,
          refunds: (
            await request<{ refunds: Refund[] }>(`/payments/${p.id}/refunds?pageSize=50`, {
              signal,
            })
          ).refunds,
        })),
      );
      return { order, payments: paymentList.payments, refunds };
    },
    [id, revision],
  );
  if (!auth.session)
    return (
      <p>
        <Link to="/login">Sign in</Link> to view this order.
      </p>
    );
  const act = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      setRevision((x) => x + 1);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  const createPayment = () =>
    act(() => ensurePayment(auth.session!.user.userId, id!, auth.request));
  return (
    <>
      <Link to="/orders">← Your orders</Link>
      <h1>Order</h1>
      <p className="identifier">{id}</p>
      <ErrorNotice error={state.error || error} />
      {state.loading && <p>Loading order…</p>}
      {state.data && (
        <>
          <div className="panel">
            <span className="badge">{state.data.order.status}</span>
            <OrderItems order={state.data.order} />
            {state.data.order.status === 'PendingPayment' && (
              <button
                className="secondary"
                disabled={busy}
                onClick={() =>
                  void act(() => auth.request(`/orders/${id}/cancel`, { method: 'POST' }))
                }
              >
                Cancel order
              </button>
            )}
          </div>
          <section className="panel">
            <h2>Payments</h2>
            <p>Pending means no completed charge. This project has no real payment provider.</p>
            {state.data.payments.map((p) => (
              <div key={p.id} className="payment-row">
                <p>
                  <strong>{p.status}</strong> ·{' '}
                  {Number.isSafeInteger(p.amountCents)
                    ? formatMoney(p.amountCents, p.currency)
                    : 'Unsupported amount'}{' '}
                  · <span className="identifier">{p.id}</span>
                </p>
                {config.data?.paymentSimulationEnabled && p.status === 'Pending' && (
                  <div className="notice">
                    <p>Development only · Local payment simulation</p>
                    {['success', 'failure', 'timeout'].map((outcome) => (
                      <button
                        key={outcome}
                        className="secondary"
                        disabled={busy}
                        onClick={() =>
                          void act(() =>
                            auth.request(`/dev/payments/${p.id}/simulate`, {
                              method: 'POST',
                              body: { outcome },
                            }),
                          )
                        }
                      >
                        Simulate {outcome}
                      </button>
                    ))}
                  </div>
                )}
                {state
                  .data!.refunds.find((r) => r.payment.id === p.id)
                  ?.refunds.map((r) => (
                    <p key={r.id}>
                      Refund {r.status}:{' '}
                      {Number.isSafeInteger(r.amountCents)
                        ? formatMoney(r.amountCents, r.currency)
                        : 'Unsupported amount'}
                    </p>
                  ))}
              </div>
            ))}
            {!state.data.payments.length && <p>No payment attempt recorded.</p>}
            {state.data.order.status === 'PendingPayment' &&
              (!state.data.payments.some((p) => p.status === 'Pending' || p.status === 'Unknown') ||
                OperationKeys.has(auth.session.user.userId, 'payment', id!)) && (
                <button disabled={busy} onClick={() => void createPayment()}>
                  {OperationKeys.has(auth.session.user.userId, 'payment', id!)
                    ? 'Retry previous payment creation'
                    : 'Create pending payment'}
                </button>
              )}
            <p className="muted">
              Opening this order reads payments without creating another attempt.
            </p>
          </section>
          <button className="secondary" disabled={busy} onClick={() => setRevision((x) => x + 1)}>
            Refresh order
          </button>
        </>
      )}
    </>
  );
}
