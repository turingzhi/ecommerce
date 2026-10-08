import { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import { ErrorNotice } from '../../components/ErrorNotice';
import type { AdminPaymentPage } from '../../api/types';
import {
  AdminReadAccess,
  AdminReadPagination,
  AdminOrderLink,
  adminMoney,
} from './AdminReadComponents';

export function PaymentAdminPage() {
  const auth = useAuth();
  const allowed = !!auth.session?.user.permissions.includes('payments:read');
  const [search, setSearch] = useSearchParams();
  const orderId = search.get('orderId') ?? '';
  const [status, setStatus] = useState(''),
    [page, setPage] = useState(1),
    [revision, setRevision] = useState(0);
  const params = new URLSearchParams({ status, page: String(page), pageSize: '20' });
  if (orderId) params.set('orderId', orderId);
  const state = usePrivateRequest(
    (request, signal) =>
      allowed
        ? request<AdminPaymentPage>(`/admin/payments?${params}`, { signal })
        : Promise.resolve(null),
    [allowed, status, page, orderId, revision],
  );
  if (!allowed) return <AdminReadAccess permission="payments:read" />;
  return (
    <>
      <h1>Payments administration</h1>
      <p>View payment attempts across customers. Records are read only.</p>
      {orderId && (
        <div className="notice admin-detail">
          Filtering order <AdminOrderLink id={orderId} />{' '}
          <button
            className="secondary compact"
            onClick={() => {
              setSearch({});
              setPage(1);
            }}
          >
            Show all payments
          </button>
        </div>
      )}
      <div className="panel admin-filters">
        <label>
          Payment status
          <select
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setPage(1);
            }}
          >
            <option value="">All statuses</option>
            {['Pending', 'Succeeded', 'Failed', 'Unknown'].map((value) => (
              <option key={value}>{value}</option>
            ))}
          </select>
        </label>
        <button className="secondary" onClick={() => setRevision((value) => value + 1)}>
          Refresh payments
        </button>
      </div>
      <ErrorNotice error={state.error} />
      {state.loading && <p role="status">Loading payments…</p>}
      {state.data && (
        <>
          <div className="panel admin-records">
            {state.data.payments.map((payment) => (
              <article className="admin-record" key={payment.id}>
                <Link className="identifier" to={`/admin/payments/${payment.id}`}>
                  {payment.id}
                </Link>
                <span className="badge">{payment.status}</span>
                <strong>{adminMoney(payment.amountCents, payment.currency)}</strong>
                <p>
                  Customer: <span className="identifier">{payment.customerId}</span>
                </p>
                <p>
                  Order: <AdminOrderLink id={payment.orderId} />
                </p>
                <time>{new Date(payment.createdAt).toLocaleString()}</time>
              </article>
            ))}
            {!state.data.payments.length && <p>No matching payments.</p>}
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
