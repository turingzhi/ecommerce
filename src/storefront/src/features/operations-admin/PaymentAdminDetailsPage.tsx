import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import { ErrorNotice } from '../../components/ErrorNotice';
import type { AdminPayment } from '../../api/types';
import { AdminReadAccess, AdminOrderLink, adminMoney } from './AdminReadComponents';

export function PaymentAdminDetailsPage() {
  const { id } = useParams();
  const auth = useAuth();
  const allowed = !!auth.session?.user.permissions.includes('payments:read');
  const [revision, setRevision] = useState(0);
  const state = usePrivateRequest(
    (request, signal) =>
      allowed ? request<AdminPayment>(`/admin/payments/${id}`, { signal }) : Promise.resolve(null),
    [allowed, id, revision],
  );
  if (!allowed) return <AdminReadAccess permission="payments:read" />;
  return (
    <>
      <Link to="/admin/payments">Back to all payments</Link>
      <h1>Payment details</h1>
      <button className="secondary" onClick={() => setRevision((value) => value + 1)}>
        Refresh payment
      </button>
      <ErrorNotice error={state.error} />
      {state.loading && <p role="status">Loading payment…</p>}
      {state.data && (
        <section className="panel admin-detail">
          <p>
            Payment: <span className="identifier">{state.data.id}</span>
          </p>
          <p>
            Order: <AdminOrderLink id={state.data.orderId} />
          </p>
          <p>
            Customer: <span className="identifier">{state.data.customerId}</span>
          </p>
          <p>
            Status: <span className="badge">{state.data.status}</span>
          </p>
          <p>
            Amount: <strong>{adminMoney(state.data.amountCents, state.data.currency)}</strong>
          </p>
          <p>Created: {new Date(state.data.createdAt).toLocaleString()}</p>
        </section>
      )}
    </>
  );
}
