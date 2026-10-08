# Local demo

Run commands from the repository root with the [Docker stack](docker.md) running.
Payments are simulated, so this walkthrough does not move real money.

## Successful flow: product to order to payment attempt

1. Open [the storefront](http://127.0.0.1:5088/), register, and sign in.
2. Choose a product with stock, set its quantity, and open Cart.
3. Check out. The API creates an order using current SQL prices and reserves stock.
4. Open the order to see its saved items and payment attempt. A Pending attempt
   means it awaits an outcome; it is not a successful charge.
5. Refresh or retry an uncertain operation using its original idempotency key.
   A successful retry returns the saved order/payment without repeating the write.

For a manual HTTP walkthrough, use [auth](../requests/auth.http),
[cart](../requests/cart.http), [orders](../requests/orders.http), and
[payments](../requests/payments.http). Replace IDs and tokens with your own values.

To continue through payment success, delivery, and a return, enable
[Development simulation](payments.md#development-simulation), then follow
[fulfillment](fulfillment.md). Admin actions need [permissions](admin.md).

## Outage and recovery: Elasticsearch goes offline

Create a dedicated product for this demo with the [catalog CLI](product-sync.md).
Then stop search:

```sh
docker compose stop elasticsearch
```

Search returns 503 on a cache miss, while SQL-backed product details and checkout
can still work. A previously cached search may return until its 30-second TTL ends.
Update the demo product through Products admin or the catalog CLI. Its SQL write
can commit while the search event waits for recovery.

Restart search:

```sh
docker compose up -d --wait elasticsearch
```

Wait for consumer retries, then search for the updated product. Inspect
[Outbox and broker delivery](docker.md#inspect-event-delivery) if it stays missing.
Always restore Elasticsearch after the exercise, including if a step fails.

## More exercises

- [Redis](redis.md): cache hit/miss, expiration, invalidation, and an outage.
- [Metrics and traces](observability.md): request duration and event propagation.
- [Verification](verification.md): repeatable API and browser checks.
- [Demo catalog maintenance](../tools/demo/README.md): refresh retained test-product names.
