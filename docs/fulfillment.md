# Fulfillment and returns

[Documentation index](README.md) · [API reference](api.md#fulfillment-and-returns) · [Admin access](admin.md) · [Payments and refunds](payments.md)

A paid order produces a shipment asynchronously. Operators record shipping and delivery. Customers may then request one whole-order return; operators receive it, refund it, and explicitly complete settlement. Order status remains `Paid`, payment status remains `Succeeded`, and fulfillment/returns/refunds never automatically restock inventory. This project has no carrier, address, warehouse-inspection, or payment-provider integration.

## Shipments

Payment success saves an `OrderPaid` event in the SQL Outbox. After confirmed RabbitMQ delivery, the consumer validates the event against a `Paid` order and its matching `Succeeded` payment, then creates one `Pending` shipment. A payment response can arrive before the shipment exists.

| Customer route | Result |
| --- | --- |
| `GET /orders/{orderId}/shipment` | Current shipment; 404 while absent |
| `GET /orders/{orderId}/tracking` | `{orderId,orderStatus,shipment,history}`; null shipment and empty history if not created |

Both require the order owner's bearer token: 401 without authentication, 404 for missing/unowned orders, and no-store for responses. Shipment fields are `id,orderId,status,createdAt,trackingNumber,shippedAt,deliveredAt`. Dates are UTC with `Z`; tracking/status timestamps start null. Reads do not consume write quotas.

Already processed historical paid events are not backfilled. Demonstrate creation with a new successful payment after rebuilding. See [shipment requests](../requests/shipments.http).

## Storage and duplicate delivery

A unique order index permits one shipment per order; foreign keys protect its order. The consumer checks nonempty matching envelope/payload order and payment IDs. It locks the order row with `UPDLOCK/HOLDLOCK` and commits the shipment, initial `Pending` history, and processed-message marker in one SQL transaction.

The same processed event ID is ignored. A different valid ID records its marker but retains the existing shipment and history. A failed marker/history save rolls back new effects for safe retry. SQL outages follow consumer retry handling; malformed/ineligible events use bounded retries and dead lettering. See [RabbitMQ](rabbitmq.md).

Refunding a paid order does not change its shipment. Stock remains reserved by the original checkout.

## Admin shipment updates

Use a configured [default admin](admin.md#default-account), or register an operator and grant only shipment access:

```sh
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --grant-shipment-admin operator@example.com
```

The account must already exist. Grants are repeat-safe, have no public HTTP equivalent, and require a new login to refresh the token's `permission=shipments:manage` claim. This permission applies across customers but does not broaden customer routes or grant return access.

| Admin route | Result |
| --- | --- |
| `GET /admin/shipments` | `{page,pageSize,shipments}`; use an entry's ID for updates |
| `GET /admin/shipments/{shipmentId}/history` | `{shipmentId,history}` |
| `PUT /admin/shipments/{shipmentId}/status` | Current shipment after transition/replay |

```http
PUT /admin/shipments/SHIPMENT_ID/status
Authorization: Bearer <adminAccessToken>
Content-Type: application/json

{"status":"Shipped","trackingNumber":"TRACK-001"}
```

Then send `{ "status": "Delivered" }` to the same route. Shipment state progresses `Pending → Shipped → Delivered`; no skipping, reversal, or reset is allowed.

`Shipped` requires a trimmed tracking number of 1–100 characters without control characters. `Delivered` may omit it; a supplied number must match the saved value. Tracking cannot be replaced after shipping. Repeated `Shipped` with the same number or repeated `Delivered` returns 200, preserving timestamps and history. Concurrent matching updates replay; conflicting tracking numbers produce one winner and one 409.

Admin routes exist in default and Development environments. Anonymous requests return 401, missing permission 403 before lookup, missing shipment 404, invalid status/tracking 400, and state/tracking conflicts 409. Valid reset-to-`Pending` requests return 409. Responses use no-store. Status and history commit atomically under the shipment-row lock.

## Lists, history, and customer tracking

Admin shipment lists accept exact case-sensitive `status=Pending|Shipped|Delivered`, page, and pageSize. Defaults are 1/20, nonpositive values become 1, size caps at 50, and extreme offsets return empty pages. Results sort creation date then ID descending. Blank/padded/unknown status and malformed numeric values return 400. Pagination is not a snapshot.

Admin history sorts by increasing internal ID. Entries have `id,fromStatus,toStatus,occurredAt,actorId`; the initial `Pending` entry has null previous status and actor. Manual transitions take the actor from authenticated identity. Replays add no entries. Existing shipments have no backfilled history.

Customer tracking exposes only `fromStatus,toStatus,occurredAt`, omitting actor and internal history IDs. Shipment and timeline are read consistently in one SQL transaction that serializes status/history writes.

## Request a whole-order return

Returns progress `Requested → Approved → Received → Completed`. A request covers the whole order without item quantities. Eligibility requires an owned `Paid` order, a `Succeeded` payment, a `Delivered` shipment, and some original amount not yet successfully refunded.

```http
POST /orders/ORDER_ID/returns
Authorization: Bearer <accessToken>
Idempotency-Key: return-001
Content-Type: application/json

{"reason":"Damaged"}
```

Reasons trim to 1–500 characters. Keys must be nonblank and at most 100 characters; they are scoped to the order. One request per order is allowed. Same key/normalized reason returns the original request even after completion (200 replay, 201 new). Another key/reason returns 409. Invalid input returns 400, missing/unowned orders 404, and ineligible states 409. Creation shares the 30/customer/minute order quota.

`GET /orders/{orderId}/return` returns the owner's request and live financial totals, or 404 if absent/unowned. Fields are `id,orderId,paymentId,reason,status,createdAt,approvedAt,receivedAt,completedAt,currency,originalAmountCents,refundedCents,reservedRefundCents,remainingRefundableCents`. Times are UTC with `Z`, or null before transition.

`Succeeded` refunds count as refunded; `Pending`/`Unknown` count as reserved; `Failed` counts as neither. Remaining equals original minus refunded minus reserved. Reads use the payment lock so totals are consistent with refund outcomes. See [return requests](../requests/returns.http).

## Admin return updates

Grant an existing operator return access, then log in again:

```sh
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll --grant-return-admin operator@example.com
```

`permission=returns:manage` is independent of shipment access. All routes below require it and use no-store. Anonymous callers receive 401, signed-in callers without the claim 403, invalid input 400, missing records 404, and state/eligibility conflicts 409.

| Route | Contract |
| --- | --- |
| `GET /admin/returns` | `{page,pageSize,returns}`; optional exact `status`, page, pageSize |
| `PUT /admin/returns/{returnId}/status` | `{ "status": "Approved" }`, then `Received`, then eligible `Completed`; 200 transition/replay |
| `POST /admin/returns/{returnId}/refund` | `{ "amountCents": 2000 }` + key; 201 new, 200 replay |

Return lists use the same 1/20, maximum-50 pagination as shipments and sort newest date then ID first. Status is exactly `Requested|Approved|Received|Completed`; blank/padded/unknown values return 400. Matching entries are rechecked after loading, so pages can shrink when returns advance during a read.

Status changes cannot skip or go backward; reset to `Requested` always returns 409. Equivalent repeats preserve transition timestamps. These admin routes have no assigned rate-limit policy.

## Return refunds and completion

New admin refunds require `Received`. They delegate to the existing [RefundService](../src/Ecommerce.Api/Features/Refunds/Services/RefundService.cs), sharing customer-refund accounting, payment-scoped keys, partial installments, unresolved blocking, and the original-charge cap. There is one ledger and no nested refund transaction.

After completion, an existing refund key/amount still replays; a new key or changed amount conflicts. To resolve local outcomes, use the payment owner's [Development refund simulator](payments.md#simulate-a-refund).

Completion is an explicit status update. It requires successful refunds equal to the original amount and no `Pending`/`Unknown` refund. An incomplete or unresolved balance keeps the return `Received`; resolve/retry refunds before completing. Historical failed attempts do not prevent completion after the full amount has succeeded.

Return creation/status writes lock order, payment, then return. Unique order indexes and restrictive foreign keys protect requests; failed saves roll back atomically. RefundService owns its payment-row transaction. Returning/refunding leaves stock unchanged.

## Code and verification

Implementation: [shipment endpoints](../src/Ecommerce.Api/Features/Shipments/Controllers/ShipmentsController.cs), [event handler](../src/Ecommerce.Api/Features/Shipments/Services/ShipmentEventHandler.cs), [status service](../src/Ecommerce.Api/Features/Shipments/Services/ShipmentService.cs), [tracking queries](../src/Ecommerce.Api/Features/Shipments/Services/ShipmentQueryService.cs), [return endpoints](../src/Ecommerce.Api/Features/Returns/Controllers/ReturnsController.cs), [return service](../src/Ecommerce.Api/Features/Returns/Services/ReturnService.cs), and [return queries](../src/Ecommerce.Api/Features/Returns/Services/ReturnQueryService.cs).

Run HTTP checks against the running stack:

```sh
python3 tests/http/verify_shipment_http.py
python3 tests/http/verify_shipment_status_http.py
python3 tests/http/verify_fulfillment_http.py
python3 tests/http/verify_returns_http.py

docker compose -f compose.yaml -f compose.development.yaml up -d --build --wait ecommerce
python3 tests/http/verify_shipment_http.py --development
python3 tests/http/verify_shipment_status_http.py --development
python3 tests/http/verify_fulfillment_http.py --development
python3 tests/http/verify_returns_http.py --development

docker compose up -d --wait ecommerce
```

Default shipment-creation checks cancel unpaid fixtures to restore stock. Default status/fulfillment/return checks seed only their fresh SQL fixtures and confirm simulator absence. Development exercises real paid-event consumption and simulated refund outcomes. Checks retain test accounts/operator claims and commerce rows for inspection; they do not grant your personal account. Re-login after recreating the API.

With `ECOMMERCE_SQLSERVER` configured as in [verification](verification.md):

```sh
dotnet run --project src/Ecommerce.Api -- --verify-shipments
dotnet run --project src/Ecommerce.Api -- --verify-returns
```

Each creates and deletes only its own GUID database. Checks cover constraints, ownership, history, concurrency, replay, eligibility, rollback/retry, live totals, and settlement races. The `--verify-rabbitmq` runner also checks paid-event delivery after consumer downtime and duplicate IDs; stop the API before shared-queue checks and restart it afterward using [the verification procedure](verification.md).
