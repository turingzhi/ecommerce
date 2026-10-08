# Shipment administration, tracking, and returns

## Goal and scope

The user requested all four previously proposed additions: an admin shipment list, shipment status history, customer order tracking, and return requests. Complete a usable workflow on top of the existing shipment, Identity, payment, and refund features, with SQL integrity, owner privacy, repeat-safe writes, tests, examples, and documentation.

Work in /Users/kaizhi/Documents/dot-beginner/Ecommerce, where the preceding accepted features exist. Preserve all current uncommitted changes. The chat worktree has an older baseline. No payment provider or carrier integration is introduced; financial outcomes continue to use the project's existing Development simulator.

## Approach

Extend the existing API and ShopDb. Keep ShipmentService responsible for shipment transitions, add a focused ReturnService for physical return states, and reuse RefundService for financial creation and limits. SQL remains authoritative; no new queues, services, or dependencies are needed.

A separate fulfillment service would introduce deployment, messaging, and ownership coordination without helping this learning workflow. Creating four disconnected implementations would duplicate authorization and refund rules. The integrated extension keeps business responsibilities separate while reusing established infrastructure.

Assumptions selected for review: returns cover the whole order, once per order; customers supply a reason; the existing order/payment stay Paid/Succeeded; recording receipt does not automatically replenish stock. Item-level eligibility, rejection/cancellation, return deadlines, carrier labels, and inventory inspection/restocking policies are later work.

## 1. Admin shipment list

GET /admin/shipments?status=Pending&page=1&pageSize=20 uses the existing ShipmentAdmin policy (permission=shipments:manage). Anonymous callers get 401; signed-in callers without that permission get 403.

Return {page,pageSize,shipments:[ShipmentResponse]}. Status is optional; allowed case-sensitive filters are Pending, Shipped, Delivered. Unknown filters return 400. Normalize page to at least 1 and pageSize to 1–50 (default 20). Calculate offsets in long; an offset beyond int.MaxValue returns an empty page. Order newest first by CreatedAt, then Id, both descending. Query AsNoTracking with cancellation, and return Cache-Control: no-store. Listing does not consume customer order/payment write quotas and does not expose account credentials or tokens.

The existing owner shipment read and admin status update retain their contracts.

## 2. Atomic shipment status history

Add ShipmentHistory with a SQL-generated long Id, required ShipmentId foreign key, nullable FromStatus, required ToStatus, UTC OccurredAt, and nullable ActorId. Index ShipmentId plus Id for timeline reads; protect the parent with a restrictive foreign key. ActorId is an immutable recorded identity string, not a user foreign key, so historical actor information survives account changes.

For newly consumed OrderPaid events, insert the initial Pending entry (FromStatus null, ActorId null for system creation) in the same SQL transaction as shipment creation and the processed marker. Same-ID or different-ID event replay must not create another entry or reset fulfillment progress.

For a successful admin Shipped or Delivered transition, insert one history row using the authenticated admin's identity in the existing shipment-row transaction. Accept actor identity from server authentication, never from the request body. Equivalent repeated writes, rejected transitions, cancellation, or failed database saves add no entry. Concurrency must yield only the entries belonging to committed transitions.

History begins after this upgrade. Do not invent actors or backfill transitions for existing shipments. Preserve their current status, tracking, and timestamps. Their timeline can therefore start later than creation.

GET /admin/shipments/{shipmentId:guid}/history uses ShipmentAdmin and returns {shipmentId,history:[{id,fromStatus,toStatus,occurredAt,actorId}]}, ordered by SQL Id ascending. Missing shipment returns 404. The state machine permits at most three history entries per shipment, so this read does not need pagination. UTC timestamps serialize with Z; responses use no-store.

## 3. Customer order tracking

GET /orders/{orderId:guid}/tracking requires authentication and filters by Orders.CustomerId. Missing or nonowned orders return 404. Return {orderId,orderStatus,shipment,history}; shipment is the existing ShipmentResponse or null, and history contains only {fromStatus,toStatus,occurredAt}, oldest first. Customer responses never include admin IDs or history actor information.

An owned order without a shipment returns 200 with shipment=null and history=[]. The existing /shipment endpoint continues returning 404 before fulfillment. Use a SQL RepeatableRead transaction across the owned-order, shipment, and history reads so concurrent status changes cannot produce mismatched current status and timeline; this does not require enabling database snapshot isolation. Use cancellation, UTC normalization, no-store, and no customer write quota.

## 4. Whole-order returns

Add ReturnRequest with Guid Id, required unique OrderId, required PaymentId, IdempotencyKey (maximum 100), Reason (trimmed 1–500 characters), Status initially Requested, CreatedAt UTC, and nullable ApprovedAt, ReceivedAt, CompletedAt. Add restrictive foreign keys to order/payment, unique OrderId, and a list index on Status/CreatedAt/Id. Derive ownership through the order; no client-supplied customer ID.

POST /orders/{orderId:guid}/returns requires the owner and Idempotency-Key, with body {reason}. Require a Delivered shipment and a Succeeded payment belonging to the Paid order, with some original charge not yet successfully refunded. The full order is returned, including when an earlier partial refund exists. No item quantities or refund amount are accepted here.

Use the same explicit lock order on every return creation/status write: order, then associated payment, then existing return row (UPDLOCK/HOLDLOCK). Resolve immutable relationship IDs first without write locks, then recheck ownership, eligibility, relationships, and existing return under those locks. Never hold a return-row write lock while attempting to acquire an earlier order/payment lock. Same key and normalized reason replay the original return (200); first creation returns 201. Same key with changed reason or another return key for that order returns 409. Missing/nonowned order returns 404; invalid reason/key returns 400; ineligible state returns 409. Replay must still work after completion or later refund outcomes. A unique OrderId constraint backs up locking.

GET /orders/{orderId:guid}/return is owner-only, 404 when missing/nonowned, otherwise the current return DTO. Include return identifiers, reason/status/timestamps, currency, original payment amount, successfully refunded amount, unresolved reserved amount, and remaining refundable amount. Financial totals are read from SQL rather than stored as a second accounting system; never expose admin identities. Use the existing customer order write policy on creation and no write policy on reads.

## Return administration and financial integration

Add ReturnAdmin policy requiring permission=returns:manage. Add --grant-return-admin REGISTERED_EMAIL using the same explicit operator CLI pattern as shipment administration. Existing shipment permissions do not silently gain refund authority. No public endpoint grants either permission. Repeating grants is safe; a fresh login is needed to acquire the claim in a bearer token.

GET /admin/returns?status=Requested&page=1&pageSize=20 lists returns with the same bounds, long-offset protection, stable newest-first ordering, no-store, and authorization behavior as the shipment list. Valid filters are Requested, Approved, Received, Completed.

PUT /admin/returns/{returnId:guid}/status accepts {status}, with forward states Requested → Approved → Received → Completed. Each transition follows the shared order → payment → return lock order and sets its corresponding UTC timestamp once. Equivalent replay preserves timestamps. Skipped/backward transitions return 409, unknown states 400, and missing return 404. Requested cannot be assigned through this update route. Received records physical receipt only; stock remains unchanged.

POST /admin/returns/{returnId:guid}/refund uses ReturnAdmin, Idempotency-Key, and {amountCents}. Permit financial creation only once the return is Received. Verify its associated payment/order, then delegate to existing RefundService.Create. Its payment-row serialization, unresolved-attempt rule, original currency, amount caps, and key replay remain authoritative. The return endpoint introduces no second refund accounting or nested transaction. Return status progression is monotonic; a completion race cannot create excess refunds because RefundService rechecks the payment balance under its own lock.

No refund ID is independently persisted on ReturnRequest. Its PaymentId links the existing refund history and financial totals. Admins can refund the remaining balance in one request or installments within the existing limits. The existing owner refund APIs and Development-only owner simulator continue working.

Completed requires total Succeeded refunds for the associated payment to equal its original AmountCents, with no Pending/Unknown attempt. Creating a Pending refund does not complete a return. Failed/Unknown outcomes leave the return Received; use existing refund retry/resolution rules, then explicitly mark Completed after successful settlement. Replaying a completed state remains safe. Completion locks/rechecks the payment accounting consistently to avoid inconsistent decisions during outcome updates.

All return/admin response timestamps use Z and private responses use no-store. Default mode exposes no financial outcome simulator.

## Migration and compatibility

Generate EF migrations for history and returns, update the snapshot, inspect foreign keys/indexes, and preserve existing rows and statuses. Do not backfill historical shipment history or create returns for existing orders. Adapt verification fixtures to supply server-side admin actor identities when calling ShipmentService directly. Existing public endpoints keep their behavior; the new tracking and return APIs are additive.

## Verification and delivery

Real SQL tests must prove list filtering/bounds/ordering, atomic history and failed-save rollback, one history entry per committed transition under races, paid-event replay after delivery, customer privacy, return uniqueness/replay/eligibility, forward transitions, stable timestamps, stock preservation, and refund/completion races and caps.

HTTP verification must use fresh owner, other-customer, shipment-admin, and return-admin accounts. Check 401/403/404 boundaries, that each admin permission grants only its intended routes, customer tracking before/after shipment, hidden actors, both list filters/pagination, and a complete Delivered → return Requested → Approved → Received → refund → Completed flow using the Development simulator. Exercise failed/Unknown refunds and retries. Default-mode checks must confirm protected routes and the continued absence of simulators; document any SQL-seeded fixtures separately.

Update CI, README capability/endpoint tables, API/workflow/architecture/verification/Docker/shipment guides, add a returns guide, and provide editable HTTP examples. Run unit, SQL, relevant broker/fulfillment, and HTTP regressions; use isolated databases and restore the default API after tests. Preserve prior changes, review the final feature independently, and report actual verification results.

## Review gate

This written design covers all four requested features as one dependency chain. After user review, create a test-first implementation plan and ask for its review/execution method before changing product code.
