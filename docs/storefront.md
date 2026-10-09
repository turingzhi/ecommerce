# Storefront

[Documentation index](README.md) · [API reference](api.md) · [Admin access](admin.md)

```sh
docker compose up -d --build --wait
```

Open [the storefront](http://127.0.0.1:5088/). ASP.NET serves React/TypeScript assets and the API from one origin. Pages use hash routes such as `/#/cart`, `/#/orders`, and `/#/admin/products`. Missing assets and unknown API paths return 404. For Vite on port 5173 with an API proxy, see [frontend setup](../src/storefront/README.md).

## Discover products

Browse current SQL products or search names/descriptions in Elasticsearch. Filter by exact case-sensitive category and inclusive price bounds. Browse sorts by product ID or price; search sorts by relevance or price, with product ID breaking ties. Defaults are page 1 and size 20, maximum size 50, within the first 10,000 results.

Product detail shows current SQL price and availability. Search and its Redis cache update asynchronously; checkout rechecks SQL. Cards use decorative SVG illustrations with a package fallback. They wrap long names/categories and preview two description lines; detail shows the full description.

## Account, cart, checkout

Register or sign in from the header. The opaque Identity bearer token stays in memory; reload signs you out. Logout, a new login, and 401 clear private page state. Late results from a previous account are discarded. A 403 shows a permission error and keeps the session.

Set an exact quantity on product detail, then edit, remove, or clear items in Cart. Cart metadata loads with at most five concurrent product reads. The displayed total is an estimate: checkout uses current SQL prices, reserves stock, and keeps the cart. The UI rejects amounts beyond its safe integer range instead of rounding them.

Checkout creates an order and then a separate `Pending` payment attempt. There is no real payment provider. Checkout, payment, and return requests use separate operation keys in `sessionStorage`, scoped to the user and resource. Bearer tokens are never saved there.

An uncertain checkout keeps its original key after cart edits or reload. Sign in again and use **Retry previous checkout** to recover the original order, even if the current cart cannot load. If order creation succeeded but payment creation is uncertain, recover/retry payment from that order. Opening an order only reads attempts. Transient failures retain keys; 429 shows `Retry-After`.

The [Development overlay](payments.md#development-simulation) enables explicitly labelled payment success/failure/timeout controls for pending attempts. Page loads never simulate an outcome. Use the API to resolve an `Unknown` attempt later.

## Orders and refunds

Order details show saved purchase prices, payment attempts, and refund status.
Eligible unpaid orders can be cancelled; Development exposes payment simulation.
Shipment tracking and return requests have been removed.

## Product administrator

Use [default setup or individual grants](admin.md) to obtain `products:manage`, then sign in again. Products admin lists, filters, and creates products with initial stock. It edits name, description, category, and price using the loaded `expectedVersion`; stock reservations are preserved.

A stale edit returns 409 and keeps your form values. Explicitly reload the current product before saving again. SQL writes are immediate; search synchronization follows asynchronously. Product deletion and stock editing are not provided. See [catalog API contracts](api.md#catalog-administration-contract).

## Order and payment operators

Separate `orders:read` and `payments:read` claims reveal Orders admin and Payments admin. These pages provide cross-customer lists/details, status filters, bounded pagination, and refresh. Both claims enable links between records. They are read only and leave customer ownership rules in place. See [admin access](admin.md#orders-and-payments).

## Catalog presentation and local demo data

Verification retains product/account/order fixtures. To give recognized fixture products readable demo details, preview and then apply:

```sh
python3 tools/demo/refresh_catalog.py
python3 tools/demo/refresh_catalog.py --apply
```

This optional local tool uses the API at port 5088 and the Compose catalog CLI. It recognizes generated verification/demo products, plus a few known original test details. Other products stay unchanged. It preserves IDs, prices, and stock, so carts and order links remain valid. SQL changes follow the normal Outbox/search flow.

Before the first update, original public details are backed up under ignored `.superpowers/demo-catalog/`. Updates run sequentially and respect catalog 429 responses. Completed updates survive interruption; rerun to finish or refresh new fixtures. This is manual demo maintenance. See [tool instructions](../tools/demo/README.md).

## Code and checks

[App routes](../src/storefront/src/App.tsx), [session handling](../src/storefront/src/auth/AuthProvider.tsx), [operation keys](../src/storefront/src/lib/operationKeys.ts), and [order details](../src/storefront/src/features/orders/OrderDetailsPage.tsx) implement these flows. Frontend unit/browser commands are in [frontend setup](../src/storefront/README.md) and [verification](verification.md). [Observability](observability.md) covers traces and metrics.
