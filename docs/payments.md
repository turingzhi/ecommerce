# Payments and refunds

[Documentation index](README.md) · [API reference](api.md) · [State diagrams](workflows.md)

This project records payment attempts and partial/full refunds in SQL. Outcomes are simulated; no money moves. Creation and reads exist in default Compose. Outcome simulators require the optional Development overlay. All customer routes require ownership through the authenticated order, never a client-supplied customer ID.

## Create a payment attempt

Create an order through `POST /orders` or `POST /cart/checkout`, then use its ID:

```http
POST /orders/ORDER_ID/payments
Authorization: Bearer <accessToken>
Idempotency-Key: payment-001
```

There is no body or client-supplied amount. SQL calculates the amount from saved purchase prices and the order's currency. A new attempt returns 201 with `id,orderId,amountCents,currency,status,createdAt`; status starts `Pending`. The same order/key returns 200 with that attempt's current state, even after it is resolved.

Keys must be nonblank and at most 100 characters (400 otherwise). A different key requires a `PendingPayment` order with no `Pending` or `Unknown` attempt. Business errors return 409, including missing/unowned orders under the current endpoint mapping. A failed attempt permits a new key. Retain the original key for an uncertain response.

## Development simulation

```sh
docker compose -f compose.yaml -f compose.development.yaml up -d --build --wait ecommerce
```

The overlay sets `ASPNETCORE_ENVIRONMENT=Development`. Keep both files when recreating that API. Log in again after recreation. Return to default Compose with:

```sh
docker compose up -d --wait ecommerce
```

Outside Development, both simulator routes are absent and return 404, even with a token.

## Simulate a payment

Use the payment response's ID, not the order ID:

```sh
TOKEN='PASTE_ACCESS_TOKEN_HERE'
PAYMENT_ID='PASTE_PAYMENT_ID_HERE'

curl -i -X POST "http://127.0.0.1:5088/dev/payments/$PAYMENT_ID/simulate" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  --data '{"outcome":"success"}'
```

| Lowercase outcome | Payment | Order | Outbox event |
| --- | --- | --- | --- |
| `success` | `Succeeded` | `Paid` | `OrderPaid` |
| `failure` | `Failed` | Remains `PendingPayment` | `PaymentFailed` |
| `timeout` | `Unknown` | Remains `PendingPayment` | None |

`Unknown` blocks another attempt, cancellation, and expiration; it can later resolve to success or failure. Terminal results cannot change to another outcome. Repeating the same outcome returns the current result without duplicate events; no outcome idempotency key is needed. Use a fresh order/payment to explore a different terminal outcome.

Simulation leaves stock unchanged because checkout already reserved it. Cancellation after failed attempts restores stock; paid orders cannot be cancelled. `OrderPaid` triggers asynchronous fulfillment (removed feature).

## Create a refund

The payment must be `Succeeded` and its order `Paid`. This requests 2000 cents from an owned payment with enough balance:

```sh
REFUND_KEY=$(uuidgen)

curl -i -X POST "http://127.0.0.1:5088/payments/$PAYMENT_ID/refunds" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $REFUND_KEY" \
  --data '{"amountCents":2000}'
```

A new refund returns 201 with `id,paymentId,amountCents,currency,status,createdAt`; status starts `Pending`. Copy its ID before simulation. Same payment/key/amount returns 200 with the original refund. A changed amount under that key returns 409. Use a new key for a separate refund and keep the original key/amount after an uncertain response.

Amounts are positive Int64 cents; keys are nonblank and at most 100 characters (400 otherwise). Missing/unowned payments return 404. Ineligible payments/orders, unresolved refunds, amount conflicts, and excessive amounts return 409.

### Refund balance rules

Remaining balance is the original payment minus `Succeeded`, `Pending`, and `Unknown` refunds. A `Pending` or `Unknown` refund also blocks any different key. `Failed` amounts do not consume balance and may be retried with a new key. For a 5000-cent payment, a successful 2000-cent refund leaves 3000; a later 3001 is rejected.

Refunds keep the order `Paid`, the payment `Succeeded`, and stock unchanged. Customer refunds and received-return admin refunds (removed feature) share the same ledger and cap.

## Simulate a refund

With Development active and `REFUND_ID` copied from creation:

```sh
REFUND_ID='PASTE_REFUND_ID_HERE'

curl -i -X POST "http://127.0.0.1:5088/dev/refunds/$REFUND_ID/simulate" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  --data '{"outcome":"success"}'
```

| Outcome | Refund | Event | Balance effect |
| --- | --- | --- | --- |
| `success` | `Succeeded` | `RefundSucceeded` | Counts as refunded |
| `failure` | `Failed` | `RefundFailed` | Releases the reserved amount |
| `timeout` | `Unknown` | `RefundUnknown` | Keeps the amount reserved and blocks another key |

`Unknown` may resolve to success or failure. Equivalent repeats add no events. Refund timeout emits an event; payment timeout does not.

## Read status and history

| Owner-only route | Result |
| --- | --- |
| `GET /payments/{paymentId}` | Current payment |
| `GET /orders/{orderId}/payments` | `{page,pageSize,payments}`; defaults 1/20 |
| `GET /refunds/{refundId}` | Current refund |
| `GET /payments/{paymentId}/refunds` | `{page,pageSize,refunds}`; defaults 1/10 |

```sh
curl -s "http://127.0.0.1:5088/payments/$PAYMENT_ID" \
  -H "Authorization: Bearer $TOKEN" | python3 -m json.tool
curl -s "http://127.0.0.1:5088/payments/$PAYMENT_ID/refunds?page=1&pageSize=10" \
  -H "Authorization: Bearer $TOKEN" | python3 -m json.tool
curl -s "http://127.0.0.1:5088/refunds/$REFUND_ID" \
  -H "Authorization: Bearer $TOKEN" | python3 -m json.tool
```

Reads use current SQL state, no-store, and UTC timestamps with `Z`. Histories include all statuses and sort newest date then ID first. Nonpositive pagination becomes 1, size caps at 50, malformed numbers return 400, and extreme/out-of-range pages are empty. Missing/unowned resources return 404 even for empty pages. Reads consume no write quota.

## Errors and retries

In Development, simulators return 200 for transition/repeat, 400 for invalid/missing/null outcomes, 404 for missing/unowned records, and 409 for incompatible states. Protected routes return 401 without valid authentication.

Payment creation, customer refund creation, and both simulators share 20 requests per customer per 60 seconds. Invalid requests and retries count. Wait for `Retry-After` on 429, retaining the original creation key and amount. Outcome repeats need no key. See [rate limiting](rate-limiting.md).

SQL order locks serialize payment creation/outcomes with order transitions. Payment-row locks serialize refund creation/outcomes and protect balance. State changes and their Outbox events commit together. Endpoint ownership checks run before internal outcome services.

## Code and verification

Implementation: [PaymentsController](../src/Ecommerce.Api/Features/Payments/Controllers/PaymentsController.cs), [PaymentService](../src/Ecommerce.Api/Features/Payments/Services/PaymentService.cs), [RefundsController](../src/Ecommerce.Api/Features/Refunds/Controllers/RefundsController.cs), and [RefundService](../src/Ecommerce.Api/Features/Refunds/Services/RefundService.cs). Editable examples: [payments.http](../requests/payments.http), [refunds.http](../requests/refunds.http).

```sh
docker compose up -d --wait ecommerce
python3 tests/http/verify_payment_simulation_http.py --disabled
python3 tests/http/verify_refund_http.py

docker compose -f compose.yaml -f compose.development.yaml up -d --build --wait ecommerce
python3 tests/http/verify_payment_simulation_http.py
python3 tests/http/verify_refund_http.py --development

docker compose up -d --wait ecommerce
```

Default checks cover auth/validation and absent simulators. Development checks cover ownership, outcomes, balance limits, concurrent replay, unresolved blocking, events, and unchanged stock. Fixtures remain for inspection: payment checks leave 18 of 20 product units; refund checks leave 8 of 10 because their paid orders retain stock. Re-login after API recreation. See [verification](verification.md).
