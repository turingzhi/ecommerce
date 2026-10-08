import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import type { OrderSummary } from '../../api/types';
import { ErrorNotice } from '../../components/ErrorNotice';
export function OrdersPage() {
  const auth = useAuth();
  const [page, setPage] = useState(1);
  const state = usePrivateRequest(
    (request, signal) =>
      request<{ orders: OrderSummary[] }>(`/orders?page=${page}&pageSize=10`, { signal }),
    [page],
  );
  if (!auth.session)
    return (
      <p>
        <Link to="/login">Sign in</Link> to view your orders.
      </p>
    );
  return (
    <>
      <h1>Your orders</h1>
      <ErrorNotice error={state.error} />
      {state.loading && <p>Loading orders…</p>}
      {state.data && (
        <>
          <div className="panel">
            {state.data.orders.map((o) => (
              <div className="order-row" key={o.id}>
                <Link to={`/orders/${o.id}`}>{o.id}</Link>
                <span className="badge">{o.status}</span>
                <time>{new Date(o.createdAt).toLocaleString()}</time>
              </div>
            ))}
            {!state.data.orders.length && <p>No orders on this page.</p>}
          </div>
          <div className="pagination">
            <button disabled={page === 1} onClick={() => setPage(page - 1)}>
              Previous
            </button>
            <button disabled={state.data.orders.length < 10} onClick={() => setPage(page + 1)}>
              Next
            </button>
          </div>
        </>
      )}
    </>
  );
}
