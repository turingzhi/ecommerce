import { ProductArtwork } from '../../components/ProductArtwork';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useAuth } from '../../auth/AuthProvider';
import { apiRequest } from '../../api/client';
import type { Details } from '../../api/types';
import { useLatestRequest } from '../../hooks/useLatestRequest';
import { formatMoney } from '../../lib/money';
import { ErrorNotice } from '../../components/ErrorNotice';
export function ProductDetailsPage() {
  const { id } = useParams();
  const auth = useAuth();
  const [quantity, setQuantity] = useState(1),
    [busy, setBusy] = useState(false),
    [message, setMessage] = useState(''),
    [error, setError] = useState<unknown>(null);
  const state = useLatestRequest<Details>(
    (signal) => apiRequest(`/products/${id}`, { signal }),
    [id],
  );
  return (
    <>
      <Link to="/">&#8592; Catalog</Link>
      <ErrorNotice error={state.error} />
      {state.loading && <p>Loading product&#8230;</p>}
      {state.data && (
        <section className="panel detail">
          <ProductArtwork name={state.data.name} category={state.data.category} large />
          <div>
            <p className="eyebrow">{state.data.category}</p>
            <h1>{state.data.name}</h1>
            <p>{state.data.description}</p>
            <p className="price">
              {Number.isSafeInteger(state.data.priceCents)
                ? formatMoney(state.data.priceCents, state.data.currency)
                : 'Unsupported price range'}
            </p>
            <p>{state.data.available} currently available. Checkout confirms stock and price.</p>
            {auth.session ? (
              <form
                onSubmit={async (e) => {
                  e.preventDefault();
                  setBusy(true);
                  setError(null);
                  setMessage('');
                  try {
                    await auth.request(`/cart/items/${id}`, { method: 'PUT', body: { quantity } });
                    setMessage('Cart quantity updated.');
                  } catch (e) {
                    setError(e);
                  } finally {
                    setBusy(false);
                  }
                }}
              >
                <label>
                  Quantity
                  <input
                    type="number"
                    min={1}
                    max={100}
                    required
                    value={quantity}
                    onChange={(e) => setQuantity(Number(e.target.value))}
                  />
                </label>
                <button disabled={busy || !Number.isSafeInteger(state.data.priceCents)}>
                  {busy ? 'Updating&#8230;' : 'Set cart quantity'}
                </button>
                <p role="status">{message}</p>
                <ErrorNotice error={error} />
                <Link to="/cart">Open cart</Link>
              </form>
            ) : (
              <Link className="button" to="/login">
                Sign in to add to cart
              </Link>
            )}
          </div>
        </section>
      )}
    </>
  );
}
