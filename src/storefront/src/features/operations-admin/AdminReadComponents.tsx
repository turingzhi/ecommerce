import { Link } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { formatMoney } from '../../lib/money';

export function AdminReadAccess({ permission }: { permission: string }) {
  return (
    <section className="panel">
      <h1>Operations administration</h1>
      <p>Sign in with {permission} permission to use this page.</p>
      <Link to="/login">Sign in</Link>
    </section>
  );
}

export function AdminReadPagination({
  page,
  total,
  pageSize,
  change,
}: {
  page: number;
  total: number;
  pageSize: number;
  change: (page: number) => void;
}) {
  return (
    <div className="pagination">
      <button disabled={page === 1} onClick={() => change(page - 1)}>
        Previous
      </button>
      <span>
        Page {page} · {total} records
      </span>
      <button disabled={page * pageSize >= total} onClick={() => change(page + 1)}>
        Next
      </button>
    </div>
  );
}

export function AdminOrderLink({ id }: { id: string }) {
  const allowed = useAuth().session?.user.permissions.includes('orders:read');
  return allowed ? (
    <Link className="identifier" to={`/admin/orders/${id}`}>
      {id}
    </Link>
  ) : (
    <span className="identifier">{id}</span>
  );
}

export function adminMoney(cents: number, currency: string): string {
  try {
    return formatMoney(cents, currency);
  } catch {
    return 'Amount exceeds the supported display range';
  }
}
