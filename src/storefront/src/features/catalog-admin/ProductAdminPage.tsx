import { useState } from 'react';
import { useAuth } from '../../auth/AuthProvider';
import { usePrivateRequest } from '../../hooks/usePrivateRequest';
import type { AdminProduct, Page } from '../../api/types';
import { ProductForm } from './ProductForm';
import { ErrorNotice } from '../../components/ErrorNotice';
export function ProductAdminPage() {
  const auth = useAuth();
  const [name, setName] = useState(''),
    [category, setCategory] = useState(''),
    [page, setPage] = useState(1),
    [revision, setRevision] = useState(0),
    [selected, setSelected] = useState<AdminProduct | null>(null),
    [create, setCreate] = useState(false),
    [error, setError] = useState<unknown>(null),
    [saved, setSaved] = useState(false);
  const allowed = auth.session?.user.permissions.includes('products:manage');
  const state = usePrivateRequest(
    (request, signal) =>
      allowed
        ? request<Page<AdminProduct>>(
            `/admin/products?${new URLSearchParams({ name, category, page: String(page), pageSize: '20' })}`,
            { signal },
          )
        : Promise.resolve({ products: [], total: 0, page: 1, pageSize: 20 }),
    [allowed, name, category, page, revision],
  );
  if (!allowed)
    return (
      <section className="panel">
        <h1>Catalog administration</h1>
        <p>Sign in with products:manage permission to use this page.</p>
      </section>
    );
  return (
    <>
      <h1>Catalog administration</h1>
      <p>Create products with initial stock, or edit details with a version check.</p>
      <div className="filters panel">
        <label>
          Name filter
          <input
            maxLength={200}
            value={name}
            onChange={(e) => {
              setName(e.target.value);
              setPage(1);
            }}
          />
        </label>
        <label>
          Exact category
          <input
            maxLength={200}
            value={category}
            onChange={(e) => {
              setCategory(e.target.value);
              setPage(1);
            }}
          />
        </label>
        <button
          onClick={() => {
            setCreate(true);
            setSelected(null);
          }}
        >
          New product
        </button>
      </div>
      <ErrorNotice error={state.error || error} />
      {saved && <p role="status">Saved in SQL. Search updates asynchronously.</p>}
      {state.loading && <p>Loading products...</p>}
      {state.data && (
        <>
          <div className="panel">
            {state.data.products.map((p) => (
              <div className="order-row" key={p.id}>
                <button
                  className="secondary"
                  onClick={() => {
                    setSelected(p);
                    setCreate(false);
                  }}
                >
                  {p.name}
                </button>
                <span>{p.category}</span>
                <span>{p.available} available</span>
              </div>
            ))}
            {!state.data.products.length && <p>No matching products.</p>}
          </div>
          <div className="pagination">
            <button disabled={page === 1} onClick={() => setPage(page - 1)}>
              Previous
            </button>
            <button disabled={page * 20 >= state.data.total} onClick={() => setPage(page + 1)}>
              Next
            </button>
          </div>
        </>
      )}
      {(create || selected) && (
        <section className="panel narrow">
          <h2>{selected ? 'Edit product' : 'Create product'}</h2>
          <ProductForm
            key={selected ? `${selected.id}:${selected.version}:${revision}` : 'create'}
            product={selected ?? undefined}
            save={async (body) => {
              setSaved(false);
              if (selected) {
                const saved = await auth.request<AdminProduct>(`/admin/products/${selected.id}`, {
                  method: 'PUT',
                  body,
                });
                setSelected(saved);
              } else await auth.request('/admin/products', { method: 'POST', body });
              setSaved(true);
              setRevision((x) => x + 1);
            }}
            reload={
              selected
                ? async () => {
                    setError(null);
                    try {
                      const fresh = await auth.request<Page<AdminProduct>>(
                        `/admin/products?name=${encodeURIComponent(selected.name)}&pageSize=50`,
                      );
                      const product = fresh.products.find((p) => p.id === selected.id);
                      if (!product)
                        throw Error(
                          'Product not found in filtered page. Clear filters and choose it again.',
                        );
                      setSelected(product);
                      setRevision((x) => x + 1);
                    } catch (e) {
                      setError(e);
                    }
                  }
                : undefined
            }
          />
        </section>
      )}
    </>
  );
}
