# HTTP API reference

[Documentation index](README.md) · [Project overview](../README.md)

The examples use the Compose API at `http://127.0.0.1:5088`. Editable requests are in [auth.http](../Http/auth.http), [orders.http](../Http/orders.http), [payments.http](../Http/payments.http), and [products.http](../Http/products.http). The API uses ASP.NET Core Identity bearer tokens for protected routes; these are Identity's built-in tokens, not JWTs.

## Authentication

Register with `POST /auth/register`, then log in with `POST /auth/login?useCookies=false`. Both accept a JSON body with `email` and `password`. Copy the login response's `accessToken` into the `Authorization: Bearer <token>` header for order and payment requests. Health and product search are anonymous.

The server reads the customer ID from the validated identity claim. Clients do not send a customer ID in order or payment requests. A missing or invalid token returns `401` before protected endpoint code runs.

## Product search

```http
GET /products/search?q=wireless
```

Search matches product names and descriptions in Elasticsearch and returns up to 20 products with `id`, `name`, `description`, `category`, and `priceCents`. It does not check current stock. Search can lag behind a catalog update; checkout reads SQL Server.

| Condition | Response |
| --- | --- |
| Valid query with matches | `200` and a product array |
| Valid query without matches | `200` and `[]` |
| Missing, blank, or over 200 characters after trimming | `400` |
| Elasticsearch unavailable or search fails | `503` with generic ProblemDetails |

## Create and read orders

`POST /orders` requires a bearer token and an `Idempotency-Key` header of 1–100 nonblank characters. The JSON body contains 1–100 distinct products; each `productId` is positive and each `quantity` is 1–100:

```json
{
  "items": [
    { "productId": 1, "quantity": 2 },
    { "productId": 2, "quantity": 1 }
  ]
}
```

The server loads prices from SQL Server, saves them on the order items, and reserves stock atomically. Money is represented as integer cents. A new order starts as `PendingPayment`.

| Condition | Response |
| --- | --- |
| New order | `201` and an order DTO |
| Same customer, key, and items | `200` and the existing order's current DTO |
| Same key with changed items | `409` |
| Missing products or insufficient stock | `409`; no partial reservation |
| Invalid body or idempotency key | `400` |
| Missing or invalid authentication | `401` |

`GET /orders` lists only the caller's orders, newest first. `page` defaults to 1 and values below 1 become 1. `pageSize` defaults to 10 and is clamped to 1–50. `GET /orders/{id}` returns the order and its items for the owner; another customer's order and a missing order both return `404`.

Order responses omit internal fields such as `CustomerId` and `IdempotencyKey`. The list endpoint returns smaller order summaries. See [OrderEndpoints](../Endpoints/OrderEndpoints.cs) and [response DTOs](../Dtos/OrderResponse.cs).

## Create a payment attempt

```http
POST /orders/{orderId}/payments
Authorization: Bearer <accessToken>
Idempotency-Key: payment-attempt-001
```

This request has no payment amount or body. The server calculates the amount from saved order-item prices and uses the order's currency. A new attempt starts as `Pending`; creating it does not charge money or mark the order `Paid`.

| Condition | Response |
| --- | --- |
| First valid attempt for a `PendingPayment` order | `201` and a pending payment DTO |
| Same order and key again | `200` and the existing payment DTO |
| Different key while a payment is `Pending` or `Unknown` | `409` |
| Order cannot be paid, is missing, or belongs to another customer | `409` under the current endpoint mapping |
| Blank or oversized key | `400` |
| Missing or invalid authentication | `401` |

The payment endpoint currently maps all service errors to `409`, including missing or unowned orders. See [PaymentEndpoints](../Endpoints/PaymentEndpoints.cs).

## Error responses and retrying requests

The API does not yet expose one uniform error schema. Order and payment handlers return `{ "error": "..." }` for their explicit validation or business errors; search failures use ProblemDetails. Identity endpoints and framework-level binding failures can have different response shapes. Check the status code before parsing a response. See [order endpoints](../Endpoints/OrderEndpoints.cs), [payment endpoints](../Endpoints/PaymentEndpoints.cs), and [search endpoints](../Endpoints/ProductEndpoints.cs).

For an uncertain order or payment-attempt response, retry with the same `Idempotency-Key` and request details. Using a different key requests a new operation. A validation error or changed-payload conflict requires correcting the request, not repeatedly resending it unchanged.

## Health and service-only operations

`GET /health` returns `200` when SQL Server is reachable and `503` otherwise. It does not check RabbitMQ or Elasticsearch.

Payment success, failure, and unknown outcomes; cancellation; expiration; and refunds are service-level operations. Expiration also runs automatically in a background worker. There are no public HTTP routes for manually simulating an outcome, cancelling, or refunding. See the [workflow diagrams](workflows.md) for their state transitions.
