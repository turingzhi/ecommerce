import type { ReactNode } from 'react';
import { Link, NavLink, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthProvider';
export function AppShell({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const navigate = useNavigate();
  return (
    <>
      <header>
        <div className="header-inner">
          <Link className="brand" to="/">
            EC<span> / STORE</span>
          </Link>
          <nav aria-label="Main">
            <NavLink to="/">Catalog</NavLink>
            {auth.session ? (
              <>
                <NavLink to="/cart">Cart</NavLink>
                <NavLink to="/orders">Orders</NavLink>
                {auth.session.user.permissions.includes('products:manage') && (
                  <NavLink to="/admin/products">Products admin</NavLink>
                )}
                {auth.session.user.permissions.includes('orders:read') && (
                  <NavLink to="/admin/orders">Orders admin</NavLink>
                )}
                {auth.session.user.permissions.includes('payments:read') && (
                  <NavLink to="/admin/payments">Payments admin</NavLink>
                )}
                <button
                  className="secondary compact"
                  onClick={() => {
                    auth.logout();
                    navigate('/');
                  }}
                >
                  Sign out
                </button>
              </>
            ) : (
              <NavLink to="/login">Sign in</NavLink>
            )}
          </nav>
        </div>
      </header>
      <main key={`${auth.generation}:${auth.session?.user.userId ?? 'public'}`}>{children}</main>
      <footer>Everyday essentials &#183; Prices in EUR &#183; Local learning storefront</footer>
    </>
  );
}
