# HTTP API reference

[Documentation index](README.md) · [Storefront](storefront.md) · [Admin access](admin.md)

Base URL: `http://127.0.0.1:5088`. Requests and responses use JSON with camel-case fields. Product IDs are integers; order, payment, refund, shipment, and return IDs are GUIDs. Money uses integer cents. Editable examples are in [requests](../requests/README.md).

## Authentication

| Route | Request / response |
| --- | --- |
| `POST /auth/register` | `{ "email": "you@example.com", "password": "YOUR_PASSWORD" }` |
| `POST /auth/login?useCookies=false` | Same body; returns Identity's bearer-token response, including `accessToken` |
| `GET /auth/me` | Bearer token required; `{userId,email,permissions}`; `Cache-Control: no-store` |

Identity supplies the `/auth` endpoints through `MapIdentityApi`. Its bearer tokens are opaque Identity tokens, not JWTs. Send `Authorization: Bearer <accessToken>` on protected requests. The server takes the customer ID and permissions from the validated identity; clients cannot choose another owner. Public registration grants no admin permissions.

Missing or invalid authentication returns 401. Admin routes additionally require their specific permission and return 403 without it. Customer detail routes hide missing and other customers' records behind 404, except payment creation as described below. See [auth requests](../requests/auth.http) and [AccountsController](../src/Ecommerce.Api/Features/Accounts/Controllers/AccountsController.cs).

## Products

| Route | Result |
| --- | --- |
| `GET /products` | SQL browse: `{products,total,page,pageSize}` |
| `GET /products/{productId}` | SQL detail: `{id,name,description,category,priceCents,available,currency}`; 404 if absent; no-store |
| `GET /products/search?q=wireless` | Elasticsearch search: `{products,total,page,pageSize}` |

These routes are anonymous and share the catalog rate limit. Browse/search product entries contain `id`, `name`, `description`, `category`, and `priceCents`; they omit stock. Detail reads current SQL data. Checkout rechecks SQL prices and stock. Search can lag behind catalog edits.

### Product search

Search matches names and descriptions. `q` is required and trims to 1–200 characters. Redis caches complete search pages for 30 seconds; cache keys include all normalized parameters, sort, and catalog generation. A Redis outage falls back to Elasticsearch. A cache miss with unavailable Elasticsearch returns 503 ProblemDetails; a cached result can still be served. See [Redis](redis.md) and [product synchronization](product-sync.md).

### Filters and pagination

```http
GET /products/search?q=wireless&category=Audio&minPriceCents=1000&maxPriceCents=6000&page=1&pageSize=10&sort=priceAsc
```

| Parameter | Browse and search |
| --- | --- |
| `category` | Optional trimmed exact, case-sensitive category; blank means no filter; maximum 200 characters |
| `minPriceCents`, `maxPriceCents` | Optional inclusive nonnegative Int64 bounds; minimum cannot exceed maximum |
| `page`, `pageSize` | Defaults 1/20; positive page; size 1–50; `page * pageSize <= 10000` |
| `sort` | Browse: `idAsc` (default), `priceAsc`, `priceDesc`; search: `relevance` (default), `priceAsc`, `priceDesc` |

Malformed numbers, invalid filters/sorts, and unsupported pages return 400. Sorting breaks ties by product ID ascending. An empty or out-of-range page within the supported window returns 200 with an empty `products` list and the matching `total`. Pagination is a live read, not a snapshot.

### Catalog administration contract

All `/admin/products` routes require `products:manage` and use no-store.

| Route | Contract |
| --- | --- |
| `GET /admin/products` | Optional `name`, `category`, `page`, `pageSize`; `{products,page,pageSize,total}` |
| `POST /admin/products` | `{name,description,category,priceCents,available}`; 201 with admin product |
| `PUT /admin/products/{id}` | `{name,description,category,priceCents,expectedVersion}`; 200 with admin product |

Admin products include the detail fields plus `version`. Name/category trim to 1–200 characters. Description is required, may be empty, and trims to at most 4,000 characters. Price is nonnegative Int64 cents; initial stock is nonnegative Int32. Updates require a positive `expectedVersion`, preserve stock, and return 409 for a stale version. Invalid data returns 400; a missing update target returns 404. Product changes and their Outbox events commit together; a rejected update changes neither.

List `name` is a case-insensitive substring; `category` is an exact case-sensitive match. Both trim, treat blanks as no filter, and allow at most 200 characters. Lists sort by ID ascending and use the 1/20, maximum-50 pagination rules described under [admin reads](#operator-order-and-payment-reads). Product deletion and stock editing are not exposed.

Code: [public endpoints](../src/Ecommerce.Api/Features/Catalog/Controllers/ProductsController.cs), [admin endpoints](../src/Ecommerce.Api/Features/Catalog/Controllers/ProductAdminController.cs), [contracts](../src/Ecommerce.Api/Features/Catalog/Contracts), and [catalog service](../src/Ecommerce.Api/Features/Catalog/Services/ProductCatalogService.cs).

## Shopping cart

All routes require the cart owner's bearer token.

| Route | Request / success |
| --- | --- |
| `GET /cart` | `{items:[{productId,quantity}]}`, sorted by product ID; no-store |
| `PUT /cart/items/{productId}` | `{ "quantity": 2 }`; sets an exact quantity; 204 |
| `DELETE /cart/items/{productId}` | Remove item; 204 even if absent |
| `DELETE /cart` | Clear cart; 204 even if absent |
| `POST /cart/checkout` | No body; `Idempotency-Key` required; 201 new order, 200 replay |

Positive product IDs, quantities 1–100, and at most 100 distinct products are allowed. Invalid input returns 400; PUT of a missing SQL product returns 404; adding a new product to a full cart returns 409. Existing items can still be updated. Empty/expired carts return `{ "items": [] }` without creating a Redis key.

Reads and successful changes renew the seven-day inactivity TTL; rejected writes do not. Cart edits store no prices and reserve no stock. Redis failures return 503 with `{ "error": "Shopping cart is temporarily unavailable." }`; there is no fallback store.

Checkout uses current SQL prices and stock, preserves the cart, and shares the order write quota. Empty/invalid carts or keys return 400; product/stock conflicts return 409. A completed customer/key returns its original SQL order before reading Redis, even after cart edits, expiry, or Redis failure. Checkout and direct order creation share the customer's key namespace. See [cart rules and examples](cart.md) and [CartController](../src/Ecommerce.Api/Features/Cart/Controllers/CartController.cs).

## Create and read orders

```http
POST /orders
Authorization: Bearer <accessToken>
Idempotency-Key: order-001
Content-Type: application/json

{"items":[{"productId":1,"quantity":2}]}
```

The body must contain 1–100 distinct positive product IDs, each with quantity 1–100. The key must be nonblank and at most 100 characters. Invalid requests return 400. SQL saves purchase prices and reserves all stock in one transaction. A new `PendingPayment` order returns 201; the same customer/key/items returns 200 with its current state. Item order does not affect replay. Changed items under the same key, missing products, or insufficient stock return 409 without partial reservations.

| Route | Response |
| --- | --- |
| `GET /orders` | `{page,pageSize,orders}`; each summary has `id,status,createdAt,currency` |
| `GET /orders/{id}` | `{id,status,currency,createdAt,orderItems}`; 404 if missing or unowned |

Items contain `id`, `productId`, `quantity`, and `unitPriceCents`. Customer responses omit customer IDs and idempotency keys. Lists include only the caller's orders, newest creation date then ID first. Page defaults to 1; size defaults to 10 and is clamped to 1–50; nonpositive pages become 1. Malformed numeric query values return 400. The current order-list handler calculates its offset in Int32; avoid extreme page values.

Code: [OrdersController](../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs), [contracts](../src/Ecommerce.Api/Features/Orders/Contracts), and [OrderService](../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs).

## Cancel an order

`POST /orders/{orderId}/cancel` requires the owner and no body or key. It returns 200 with the order for an eligible or already-cancelled order; 404 for missing/unowned orders; 409 for blocked state, unresolved payment, or stock restoration failure.

Only `PendingPayment` orders without `Pending` or `Unknown` payments can be cancelled. The SQL transaction restores saved item quantities, marks `Cancelled`, and writes one `OrderCancelled` event. Repeats restore nothing twice. Redis is not involved. Expiration follows the same stock/unresolved-payment rules automatically; see [workflows](workflows.md).

## Create a payment attempt

`POST /orders/{orderId}/payments` requires the owner, a nonblank key of at most 100 characters, and no body. It calculates the amount from saved order-item prices and currency. Creation records `Pending`; it does not charge money or mark the order paid.

A new attempt returns 201; the same order/key returns 200 with the original attempt. A new key requires `PendingPayment` and no `Pending`/`Unknown` attempt. Invalid keys return 400. All service errors currently map to 409, including missing or unowned orders. Failed attempts allow a new key; uncertain responses should reuse the original key. See [payments](payments.md).

## Payment and refund reads

These owner-only SQL reads use no-store and do not consume a write quota. Missing or unowned records return 404. Timestamps are UTC with a `Z` suffix.

| Route | Response |
| --- | --- |
| `GET /payments/{paymentId}` | `{id,orderId,amountCents,currency,status,createdAt}` |
| `GET /orders/{orderId}/payments` | `{page,pageSize,payments}`; defaults 1/20 |
| `GET /refunds/{refundId}` | `{id,paymentId,amountCents,currency,status,createdAt}` |
| `GET /payments/{paymentId}/refunds` | `{page,pageSize,refunds}`; defaults 1/10 |

History lists include every status, sort by creation date then ID descending, normalize nonpositive pagination to 1, and cap size at 50. Malformed numeric values return 400; empty histories and extreme/out-of-range pages return an empty list. Ownership is checked even for empty pages.

## Refunds

`POST /payments/{paymentId}/refunds` requires the payment owner, `Idempotency-Key`, and `{ "amountCents": 2000 }`. Amounts must be positive Int64 cents; keys must be nonblank and at most 100 characters. Invalid input returns 400, missing/unowned payments 404, and eligibility/balance/unresolved/key conflicts 409.

The payment must be `Succeeded` and its order `Paid`. A new `Pending` refund returns 201; the same payment/key/amount returns 200 with the original refund. Changing that amount returns 409. `Succeeded`, `Pending`, and `Unknown` amounts count against the original charge; `Pending` or `Unknown` also blocks another key. Failed refunds release their reserved amount. Refunds leave the order paid and stock unchanged. See [refund examples and balances](payments.md#create-a-refund).

Code: [payment endpoints](../src/Ecommerce.Api/Features/Payments/Controllers/PaymentsController.cs), [refund endpoints](../src/Ecommerce.Api/Features/Refunds/Controllers/RefundsController.cs), [payment contracts](../src/Ecommerce.Api/Features/Payments/Contracts), and [refund contracts](../src/Ecommerce.Api/Features/Refunds/Contracts).

## Development payment simulation

`POST /dev/payments/{paymentId}/simulate` and `POST /dev/refunds/{refundId}/simulate` accept `{ "outcome": "success" }`, `failure`, or `timeout`. Both require ownership and a bearer token. They return 200 for a valid transition/repeat, 400 for invalid/missing outcomes, 404 for missing/unowned records, and 409 for incompatible transitions. They need no idempotency key.

The routes exist only in Development; default Compose returns 404. Outcomes are simulated and transfer no money. `Unknown` can later resolve to success or failure; terminal outcomes cannot be changed. See [activation and outcome rules](payments.md#development-simulation).

## Fulfillment and returns

All routes below use no-store. Owner routes require authentication; admin routes require the listed independent permission. Missing/unowned customer records return 404. Admin callers without permission receive 403 before lookup.

### Shipment read

| Route | Access / response |
| --- | --- |
| `GET /orders/{orderId}/shipment` | Owner; `{id,orderId,status,createdAt,trackingNumber,shippedAt,deliveredAt}` |
| `GET /orders/{orderId}/tracking` | Owner; `{orderId,orderStatus,shipment,history}` |
| `GET /admin/shipments` | `shipments:manage`; `{page,pageSize,shipments}` |
| `GET /admin/shipments/{shipmentId}/history` | `shipments:manage`; `{shipmentId,history}` |

`OrderPaid` consumption creates one `Pending` shipment asynchronously. The shipment read returns 404 while absent; tracking returns null shipment and empty history for an owned order without fulfillment. Customer history exposes `fromStatus,toStatus,occurredAt`; admin history adds `id,actorId`. History sorts by increasing internal ID. Dates are UTC; tracking details are null before their transitions.

### Admin shipment status

`PUT /admin/shipments/{shipmentId}/status` requires `shipments:manage`. Send `{ "status": "Shipped", "trackingNumber": "TRACK-001" }`, then `{ "status": "Delivered" }`. Status and history commit together. Repeated equivalent requests return 200 without changing timestamps or adding history. Invalid status/tracking returns 400; missing shipments 404; reset, skipped/backward states, or changed tracking 409.

Tracking trims to 1–100 characters without control characters. Shipping requires it. Delivery may omit it; a supplied value must match the saved number. See [fulfillment](fulfillment.md#admin-shipment-updates).

### Whole-order returns

| Route | Access / request / success |
| --- | --- |
| `POST /orders/{orderId}/returns` | Owner + key; `{ "reason": "Damaged" }`; 201 new, 200 replay |
| `GET /orders/{orderId}/return` | Owner; return with current refund totals |
| `GET /admin/returns` | `returns:manage`; `{page,pageSize,returns}` |
| `PUT /admin/returns/{returnId}/status` | `returns:manage`; `{ "status": "Approved" }`; 200 transition/replay |
| `POST /admin/returns/{returnId}/refund` | `returns:manage` + key; `{ "amountCents": 2000 }`; 201 new, 200 replay |

One whole-order return is allowed after a paid order's shipment is delivered and before it is fully refunded. Reasons trim to 1–500 characters; keys are nonblank, at most 100 characters, and scoped to the order. Same key/reason replays even after completion; another key/reason conflicts. Invalid input returns 400, missing records 404, and state/eligibility conflicts 409.

Returns move `Requested → Approved → Received → Completed`, one step at a time. Reset to `Requested` is rejected; repeats preserve timestamps. New admin refunds require `Received` and share the existing refund ledger/key rules. After completion, an existing refund key can replay. Completion requires the full original amount successfully refunded with no unresolved refunds. Stock, payment, and order status stay unchanged.

Return fields: `id,orderId,paymentId,reason,status,createdAt,approvedAt,receivedAt,completedAt,currency,originalAmountCents,refundedCents,reservedRefundCents,remainingRefundableCents`. Successful refunds count as refunded; `Pending`/`Unknown` as reserved; failed amounts count as neither. Remaining is original minus refunded minus reserved.

Shipment/return admin lists default to 1/20, normalize nonpositive values to 1, cap size at 50, and return empty extreme offsets. They sort newest date then ID first and accept exact case-sensitive `status`: `Pending|Shipped|Delivered` or `Requested|Approved|Received|Completed`. Blank, whitespace-padded, or unknown status values and malformed numbers return 400. Return pages may shrink if matching records advance while being read.

Code: [shipment endpoints](../src/Ecommerce.Api/Features/Shipments/Controllers/ShipmentsController.cs), [return endpoints](../src/Ecommerce.Api/Features/Returns/Controllers/ReturnsController.cs), [shipment contracts](../src/Ecommerce.Api/Features/Shipments/Contracts), and [return contracts](../src/Ecommerce.Api/Features/Returns/Contracts). See [fulfillment examples](fulfillment.md).

## Operator order and payment reads

| Route | Permission / response |
| --- | --- |
| `GET /admin/orders` | `orders:read`; `{page,pageSize,total,orders}` |
| `GET /admin/orders/{id}` | `orders:read`; order details plus `customerId` and saved items |
| `GET /admin/payments` | `payments:read`; `{page,pageSize,total,payments}` |
| `GET /admin/payments/{id}` | `payments:read`; payment details plus `customerId` |

These read across customers without broadening owner routes. Responses use no-store, UTC dates, and omit idempotency keys and Identity internals. Missing detail records return 404.

Lists accept `status`, `page`, and `pageSize`; payments also accept an `orderId` GUID. Status trims, blanks mean no filter, and values are case-sensitive: orders `PendingPayment|Paid|Cancelled`; payments `Pending|Succeeded|Failed|Unknown`. Invalid statuses, query GUIDs, or numeric values return 400.

Defaults are 1/20, nonpositive values become 1, and size is capped at 50. Lists sort newest creation date then ID first; extreme offsets return an empty list with its total. Pages and totals can change between requests. See [permission setup](admin.md#orders-and-payments), [order endpoints](../src/Ecommerce.Api/Features/Orders/Controllers/OrderAdminController.cs), and [payment endpoints](../src/Ecommerce.Api/Features/Payments/Controllers/PaymentAdminController.cs).

## Health and service-only operations

| Anonymous route | Behavior |
| --- | --- |
| `GET /health` | SQL connectivity; `{status}`; 200 healthy or 503 unhealthy |
| `GET /health/live` | No dependency checks; `{ "status": "healthy" }`; 200, no-store |
| `GET /health/dependencies` | SQL, RabbitMQ, Elasticsearch, Redis; `{status,dependencies}`; 200 if all healthy, otherwise 503; no-store |
| `GET /ui/config` | `{paymentSimulationEnabled}`; true only in Development; no-store |

Dependency reports contain generic statuses, never connection details or exceptions. See [health checks](health.md), [HealthController](../src/Ecommerce.Api/Common/Controllers/HealthController.cs), and [UiController](../src/Ecommerce.Api/Common/Controllers/UiController.cs).

## Rate limiting

| Shared policy | Default per 60 seconds | Routes |
| --- | --- | --- |
| Catalog | 120 per connection IP | Browse, search, product detail |
| Order writes | 30 per customer | Order creation, cart checkout, return requests |
| Payment writes | 20 per customer | Payment creation, customer refund creation, both Development simulators |

Cached searches, invalid requests, and retries count. Other routes, including admin return refunds, have no assigned write policy. Limits are configurable and local to the API process. A rejection returns 429, integer-second `Retry-After`, no-store, and `{ "error": "Too many requests. Please retry later." }`. It never reaches the handler. Authentication/authorization precedes customer limiting. See [rate limiting](rate-limiting.md).

## Error responses and retrying requests

Explicit business/validation errors usually use `{ "error": "..." }`; unavailable search uses ProblemDetails. Identity and framework binding errors may differ. Check status before parsing. Malformed GUID path segments fail route matching; malformed typed query/body values normally return 400.

For an uncertain write, keep its original idempotency key and request details. A fresh key requests a new operation. Cart checkout replay deliberately uses the saved order rather than a changed cart. Retry safe cart PUT/DELETE and equivalent status transitions after uncertain responses. Wait at least `Retry-After` on 429. Correct validation/state conflicts before retrying.
