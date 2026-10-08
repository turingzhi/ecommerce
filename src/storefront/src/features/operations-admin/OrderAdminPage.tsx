import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import { ErrorNotice } from '../../components/ErrorNotice';
import type { AdminOrderSummary, AdminOrderPage } from '../../api/types';
import { AdminReadAccess, AdminReadPagination } from './AdminReadComponents';

export function OrderAdminPage() {
  const auth = useAuth();
  const allowed = !!auth.session?.user.permissions.includes('orders:read');
  const [status, setStatus] = useState(''),
    [page, setPage] = useState(1),
    [revision, setRevision] = useState(0);
  const state = usePrivateRequest(
    (request, signal) =>
      allowed
        ? request<AdminOrderPage>(
            `/admin/orders?${new URLSearchParams({ status, page: String(page), pageSize: '20' })}`,
            { signal },
          )
        : Promise.resolve(null),
    [allowed, status, page, revision],
  );
  if (!allowed) return <AdminReadAccess permission="orders:read" />;
  return (
    <>
      <h1>Orders administration</h1>
      <p>View orders across customers. Records are read only.</p>
      <div className="panel admin-filters">
        <label>
          Order status
          <select
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setPage(1);
            }}
          >
            <option value="">All statuses</option>
            {['PendingPayment', 'Paid', 'Cancelled'].map((value) => (
              <option key={value}>{value}</option>
            ))}
          </select>
        </label>
        <button className="secondary" onClick={() => setRevision((value) => value + 1)}>
          Refresh orders
        </button>
      </div>
      <ErrorNotice error={state.error} />
      {state.loading && <p role="status">Loading orders…</p>}
      {state.data && (
        <>
          <div className="panel admin-records">
            {state.data.orders.map((order: AdminOrderSummary) => (
              <article className="admin-record" key={order.id}>
                <Link className="identifier" to={`/admin/orders/${order.id}`}>
                  {order.id}
                </Link>
                <span className="badge">{order.status}</span>
                <p>
                  Customer: <span className="identifier">{order.customerId}</span>
                </p>
                <time>{new Date(order.createdAt).toLocaleString()}</time>
              </article>
            ))}
            {!state.data.orders.length && <p>No matching orders.</p>}
          </div>
          <AdminReadPagination
            page={page}
            total={state.data.total}
            pageSize={state.data.pageSize}
            change={setPage}
          />
        </>
      )}
    </>
  );
}
