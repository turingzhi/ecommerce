import { ProductArtwork } from '../../components/ProductArtwork';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { apiRequest } from '../../api/client';
import type { Page, Product } from '../../api/types';
import { useLatestRequest } from '../../hooks/useLatestRequest';
import { formatMoney, parsePriceInput } from '../../lib/money';
import { ErrorNotice } from '../../components/ErrorNotice';
export function CatalogPage() {
  const [query, setQuery] = useState(''),
    [category, setCategory] = useState(''),
    [min, setMin] = useState(''),
    [max, setMax] = useState(''),
    [sort, setSort] = useState(''),
    [page, setPage] = useState(1);
  const search = query.trim().length > 0;
  const state = useLatestRequest<Page<Product>>(
    (signal) => {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: '20',
        sort: sort || (search ? 'relevance' : 'idAsc'),
      });
      if (search) params.set('q', query.trim());
      if (category.trim()) params.set('category', category.trim());
      if (min) params.set('minPriceCents', String(parsePriceInput(min)));
      if (max) params.set('maxPriceCents', String(parsePriceInput(max)));
      return apiRequest(`${search ? '/products/search' : '/products'}?${params}`, { signal });
    },
    [query, category, min, max, sort, page],
  );
  const change = (setter: (v: string) => void, value: string) => {
    setter(value);
    setPage(1);
  };
  return (
    <>
      <div className="heading">
        <div>
          <p className="eyebrow">Explore the catalog</p>
          <h1>Find your next everyday essential.</h1>
          <p>Browse current products, or search the indexed catalog.</p>
        </div>
      </div>
      <section className="filters panel">
        <label>
          Search
          <input
            type="search"
            placeholder="Try wireless"
            value={query}
            onChange={(e) => {
              change(setQuery, e.target.value);
              setSort('');
            }}
          />
        </label>
        <label>
          Category
          <input
            maxLength={200}
            value={category}
            placeholder="Exact category"
            onChange={(e) => change(setCategory, e.target.value)}
          />
        </label>
        <label>
          Minimum price
          <input
            inputMode="decimal"
            value={min}
            placeholder="EUR"
            onChange={(e) => change(setMin, e.target.value)}
          />
        </label>
        <label>
          Maximum price
          <input
            inputMode="decimal"
            value={max}
            placeholder="EUR"
            onChange={(e) => change(setMax, e.target.value)}
          />
        </label>
        <label>
          Sort
          <select value={sort} onChange={(e) => change(setSort, e.target.value)}>
            <option value="">{search ? 'Relevance' : 'Product ID'}</option>
            <option value="priceAsc">Price: low to high</option>
            <option value="priceDesc">Price: high to low</option>
          </select>
        </label>
      </section>
      <ErrorNotice error={state.error} />
      {state.loading ? (
        <p role="status">Loading products&#8230;</p>
      ) : (
        state.data && (
          <>
            <p className="muted">
              {state.data.total} products &#183; Page {page}
            </p>
            <div className="product-grid">
              {state.data.products.map((p) => (
                <article className="product-card" key={p.id}>
                  <ProductArtwork name={p.name} category={p.category} />
                  <p className="eyebrow">{p.category}</p>
                  <h2>
                    <Link to={`/products/${p.id}`}>{p.name}</Link>
                  </h2>
                  <p className="product-description">{p.description}</p>
                  <strong>
                    {Number.isSafeInteger(p.priceCents) && p.priceCents >= 0
                      ? formatMoney(p.priceCents)
                      : 'Unsupported price range'}
                  </strong>
                  <Link className="text-link" to={`/products/${p.id}`}>
                    View details &#8594;
                  </Link>
                </article>
              ))}
            </div>
            {!state.data.products.length && <p>No products match these filters.</p>}
            <div className="pagination">
              <button className="secondary" disabled={page === 1} onClick={() => setPage(page - 1)}>
                Previous
              </button>
              <button
                className="secondary"
                disabled={page * 20 >= state.data.total || page >= 500}
                onClick={() => setPage(page + 1)}
              >
                Next
              </button>
            </div>
            {search && (
              <p className="muted">
                Search updates asynchronously. Product details show current SQL price and
                availability.
              </p>
            )}
          </>
        )
      )}
    </>
  );
}
