# Shopping cart

[Documentation index](README.md) · [API reference](api.md#shopping-cart) · [Redis](redis.md)

Every cart route requires a bearer token and uses the authenticated customer's ID. Start with [auth requests](../requests/auth.http), then use [cart requests](../requests/cart.http).

## Use the cart

```http
PUT /cart/items/1
Authorization: Bearer <accessToken>
Content-Type: application/json

{"quantity":2}
```

PUT sets the exact quantity. Repeating it leaves two units. Positive product IDs and quantities 1–100 are required (400 otherwise). SQL checks product existence (404 if absent), but cart quantities may exceed stock. Adding a new product to a cart with 100 distinct items returns 409; existing items can still be updated. Cart edits reserve no stock and store no prices.

| Route | Success |
| --- | --- |
| `GET /cart` | 200 with `{items:[{productId,quantity}]}`, sorted by product ID; no-store |
| `PUT /cart/items/{productId}` | 204 |
| `DELETE /cart/items/{productId}` | 204, including absent items |
| `DELETE /cart` | 204, including an absent cart |

Empty or expired carts return `{ "items": [] }` without creating a key. Missing/invalid authentication returns 401. Redis failures return 503 with a generic cart-unavailable error; there is no alternate store. Only PUT queries SQL for product existence.

## Storage and inactivity

`cart:v1:{customerId}` is a Redis hash of product IDs to quantities. The whole hash expires after 604800 seconds: seven days without a successful application operation. Reads and successful changes refresh it. Invalid quantities, missing-product writes, and full-cart rejections do not. Removing the last item removes the key; removing an absent item refreshes a cart that still exists.

Lua scripts make read/refresh, quantity/cap/expiry changes, and removal atomic. Concurrent additions cannot both claim the last slot. A request may be cancelled after Redis has applied its command; retry the same PUT or DELETE to reach the intended state.

Compose configures no explicit persistent Redis volume. The TTL defines inactivity, not durability; container recreation can lose carts.

## Checkout

```http
POST /cart/checkout
Authorization: Bearer <accessToken>
Idempotency-Key: cart-checkout-001
```

No body is needed. The server captures the current cart, loads SQL prices, and reserves all stock in one order transaction. Success returns 201 with a `PendingPayment` order. It keeps the cart, including edits made while checkout ran.

Retry an uncertain response with the original key. A completed customer/key returns 200 with the saved order before Redis is accessed, even if the cart changed, expired, was cleared, or Redis is down. Use a fresh key for a new intended checkout. Cart checkout and `POST /orders` share the customer's key namespace.

Invalid keys (blank or over 100 characters) and empty/invalid carts return 400; missing products or insufficient stock return 409; Redis failure during a new checkout returns 503. Concurrent same-key requests are serialized in SQL. If they captured different carts, one can return 409; a later retry returns the committed order. Cart and SQL do not share a transaction, and checkout never clears the cart automatically.

Checkout shares the order policy: 30 requests per customer per 60 seconds, including invalid requests and retries. Other cart routes have no rate policy. On 429, wait for `Retry-After` and retain the original key.

You can instead send a saved `GET /cart` snapshot to `POST /orders`:

```http
POST /orders
Authorization: Bearer <accessToken>
Idempotency-Key: direct-order-001
Content-Type: application/json

{"items":[{"productId":1,"quantity":2}]}
```

Direct-order retries require the original items as well as the key. Current SQL price and stock determine a new order; missing products can remain in Redis until removed. Clear the cart separately when desired.

## Verify and inspect

```sh
docker compose up -d --build --wait ecommerce
python3 tests/http/verify_cart_http.py
python3 tests/http/verify_cart_checkout_http.py --outages
```

Checks cover ownership, exact quantities, TTL, capacity/concurrency, checkout, and retry behavior. They use their own cart fixtures; `--outages` temporarily interrupts Redis. See [verification](verification.md) for retained data and outage details.

Inspect your key with `redis-cli`:

```text
TYPE cart:v1:<your-customer-id>
HGETALL cart:v1:<your-customer-id>
TTL cart:v1:<your-customer-id>
```

These commands do not refresh expiry; application `GET /cart` does. Implementation: [CartController](../src/Ecommerce.Api/Features/Cart/Controllers/CartController.cs), [CartService](../src/Ecommerce.Api/Features/Cart/Services/CartService.cs), and [contracts](../src/Ecommerce.Api/Features/Cart/Contracts).
