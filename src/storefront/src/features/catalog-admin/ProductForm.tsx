import { useState } from 'react';
import type { AdminProduct } from '../../api/types';
import { parsePriceInput, safeInteger } from '../../lib/money';
import { ErrorNotice } from '../../components/ErrorNotice';
export function ProductForm({
  product,
  save,
  reload,
}: {
  product?: AdminProduct;
  save: (body: Record<string, unknown>) => Promise<unknown>;
  reload?: () => void;
}) {
  const valid =
    !product ||
    (Number.isSafeInteger(product.priceCents) &&
      product.priceCents >= 0 &&
      Number.isSafeInteger(product.version) &&
      product.version > 0);
  const [name, setName] = useState(product?.name ?? ''),
    [description, setDescription] = useState(product?.description ?? ''),
    [category, setCategory] = useState(product?.category ?? ''),
    [price, setPrice] = useState(
      product && valid
        ? `${BigInt(product.priceCents) / 100n}.${(BigInt(product.priceCents) % 100n).toString().padStart(2, '0')}`
        : '',
    ),
    [stock, setStock] = useState('0'),
    [busy, setBusy] = useState(false),
    [error, setError] = useState<unknown>(null),
    [saved, setSaved] = useState(false);
  return (
    <form
      onSubmit={async (e) => {
        e.preventDefault();
        setError(null);
        setSaved(false);
        setBusy(true);
        try {
          if (!valid) throw Error('Unsupported price or version range.');
          const body: Record<string, unknown> = {
            name,
            description,
            category,
            priceCents: parsePriceInput(price),
          };
          if (product) body.expectedVersion = product.version;
          else body.available = safeInteger(Number(stock));
          await save(body);
          if (!product) setSaved(true);
        } catch (e) {
          setError(e);
        } finally {
          setBusy(false);
        }
      }}
    >
      <label>
        Name
        <input required maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label>
        Category
        <input
          required
          maxLength={200}
          value={category}
          onChange={(e) => setCategory(e.target.value)}
        />
      </label>
      <label>
        Description
        <textarea
          maxLength={4000}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
      </label>
      <label>
        Price (EUR)
        <input
          required
          inputMode="decimal"
          value={price}
          onChange={(e) => setPrice(e.target.value)}
        />
      </label>
      {!product && (
        <label>
          Initial stock
          <input
            required
            type="number"
            min={0}
            max={2147483647}
            value={stock}
            onChange={(e) => setStock(e.target.value)}
          />
        </label>
      )}
      {product && (
        <p>
          Current stock: {product.available}. Editing details preserves reservations. Version:{' '}
          {product.version}.
        </p>
      )}
      <ErrorNotice error={error} />
      {!valid && <p role="alert">Unsupported price or version range.</p>}
      {saved && <p role="status">Saved in SQL. Search updates asynchronously.</p>}
      <button disabled={busy || !valid}>
        {busy ? 'Saving...' : product ? 'Save product' : 'Create product'}
      </button>
      {product && reload && (
        <button className="secondary" type="button" disabled={busy} onClick={reload}>
          Reload current product
        </button>
      )}
      {!!error && product && (
        <p>
          Edits are preserved. If another operator changed this product, reload explicitly before
          saving again.
        </p>
      )}
    </form>
  );
}
