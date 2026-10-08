import { Routes, Route, Link } from 'react-router-dom';
import { AuthProvider } from './auth/AuthProvider';
import { AppShell } from './components/AppShell';
import { CatalogPage } from './features/catalog/CatalogPage';
import { ProductDetailsPage } from './features/catalog/ProductDetailsPage';
import { LoginPage } from './features/accounts/LoginPage';
import { CartPage } from './features/cart/CartPage';
import { OrdersPage } from './features/orders/OrdersPage';
import { OrderDetailsPage } from './features/orders/OrderDetailsPage';
import { ProductAdminPage } from './features/catalog-admin/ProductAdminPage';
import { OrderAdminPage } from './features/operations-admin/OrderAdminPage';
import { OrderAdminDetailsPage } from './features/operations-admin/OrderAdminDetailsPage';
import { PaymentAdminPage } from './features/operations-admin/PaymentAdminPage';
import { PaymentAdminDetailsPage } from './features/operations-admin/PaymentAdminDetailsPage';
export default function App() {
  return (
    <AuthProvider>
      <AppShell>
        <Routes>
          <Route path="/" element={<CatalogPage />} />
          <Route path="/products/:id" element={<ProductDetailsPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/cart" element={<CartPage />} />
          <Route path="/orders" element={<OrdersPage />} />
          <Route path="/orders/:id" element={<OrderDetailsPage />} />
          <Route path="/admin/products" element={<ProductAdminPage />} />
          <Route path="/admin/orders" element={<OrderAdminPage />} />
          <Route path="/admin/orders/:id" element={<OrderAdminDetailsPage />} />
          <Route path="/admin/payments" element={<PaymentAdminPage />} />
          <Route path="/admin/payments/:id" element={<PaymentAdminDetailsPage />} />
          <Route
            path="*"
            element={
              <>
                <h1>Page not found</h1>
                <Link to="/">Back to catalog</Link>
              </>
            }
          />
        </Routes>
      </AppShell>
    </AuthProvider>
  );
}
