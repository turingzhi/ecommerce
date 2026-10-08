import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import { ErrorNotice } from '../../components/ErrorNotice';
import type { AdminOrder } from '../../api/types';
import { AdminReadAccess, adminMoney } from './AdminReadComponents';

export function OrderAdminDetailsPage() {
  const { id } = useParams();
  const auth = useAuth();
  const allowed = !!auth.session?.user.permissions.includes('orders:read');
  const [revision, setRevision] = useState(0);
  const state = usePrivateRequest(
    (request, signal) =>
      allowed ? request<AdminOrder>(`/admin/orders/${id}`, { signal }) : Promise.resolve(null),
    [allowed, id, revision],
  );
  if (!allowed) return <AdminReadAccess permission="orders:read" />;
  return (
    <>
      <Link to="/admin/orders">Back to all orders</Link>
      <h1>Order details</h1>
      <button className="secondary" onClick={() => setRevision((value) => value + 1)}>
        Refresh order
      </button>
      <ErrorNotice error={state.error} />
      {state.loading && <p role="status">Loading order…</p>}
      {state.data && (
        <section className="panel admin-detail">
          <p>
            Order: <span className="identifier">{state.data.id}</span>
          </p>
          <p>
            Customer: <span className="identifier">{state.data.customerId}</span>
          </p>
          <p>
            Status: <span className="badge">{state.data.status}</span>
          </p>
          <p>Created: {new Date(state.data.createdAt).toLocaleString()}</p>
          <h2>Items</h2>
          {state.data.orderItems.map((item) => (
            <div className="order-row" key={item.id}>
              <Link to={`/products/${item.productId}`}>Product {item.productId}</Link>
              <span>Quantity: {item.quantity}</span>
              <span>Unit price: {adminMoney(item.unitPriceCents, state.data!.currency)}</span>
            </div>
          ))}
          {auth.session?.user.permissions.includes('payments:read') && (
            <p>
              <Link to={`/admin/payments?orderId=${state.data.id}`}>
                View payments for this order
              </Link>
            </p>
          )}
        </section>
      )}
    </>
  );
}
