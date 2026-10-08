# Shipment creation from paid-order events

## Purpose and accepted scope

Make the existing RabbitMQ flow produce a visible commerce result for this learning project. When the consumer receives an OrderPaid event, create exactly one Pending shipment record for that order. The authenticated order owner can read it through GET /orders/{orderId}/shipment. Delayed delivery and duplicate messages must be safe.

The user approved this feature with “do it” after the proposed event-driven flow. This specification makes the storage, transaction, HTTP behavior, and verification concrete for review before implementation.

## Approach

Extend the existing EventConsumer with an OrderPaid handler. Keep the existing SQL Outbox, RabbitMQ publisher, consumer worker, retries, and acknowledgements. The new handler saves shipment creation and its processed-event marker in a single SQL transaction.

Alternatives considered: creating the shipment in PaymentService would make the API response immediately include fulfillment but would bypass the event-driven learning goal. A separate shipping worker and queue would provide independent deployment but would require additional topology and operation. The existing consumer gives this feature the needed behavior with the smallest change to the current architecture.

## Data model and migration

Add a Shipment model and ShopDb.Shipments, with:

- Id: generated GUID primary key.
- OrderId: required foreign key to Orders, with a unique index.
- Status: Pending.
- CreatedAt: UTC creation time.

Add an EF Core SQL Server migration and update the model snapshot. Each order may have at most one shipment. The initial feature records fulfillment eligibility; the shipment stays Pending. Carrier integration, addresses, tracking numbers, dispatch, and delivery are separate future work.

Existing Paid orders whose events were already marked processed will not be backfilled. Newly delivered, unprocessed valid OrderPaid events create shipments. The documentation must tell learners to create a new payment success when demonstrating the feature after upgrade.

## Event handling and atomicity

OrderPaid already contains OrderId and PaymentId in the payload. Validate that both IDs are nonempty and that the payload OrderId matches the envelope OrderId. Load the authoritative order and payment from SQL. Require a Paid order and a Succeeded payment belonging to it; derive ownership from SQL rather than payload customer fields.

For SQL Server, lock the order row with UPDLOCK and HOLDLOCK inside the transaction, following the existing order/payment locking pattern. Recheck the processed-message ID while holding that lock. A previously processed event returns false without changes. For a new event, check for an existing shipment for the order. Create it only when absent, then add the ProcessedMessage and commit both together. A different valid event ID for the same paid order adds its marker while retaining the existing shipment.

The unique OrderId index provides a second database constraint on duplicate creation. A failed marker write must roll back shipment insertion. A crash after SQL commit but before broker acknowledgement is safe because redelivery finds the marker. Shipment creation and processing markers remain independent of Redis and Elasticsearch.

Preserve ProductUpserted processing and existing behavior for other commerce events. Keep Elasticsearch work outside the shipment SQL transaction. Preserve the worker’s existing policy: database outages remain retryable; invalid event data uses the current bounded retry/dead-letter path. Acknowledge only after successful processing or a detected duplicate.

## HTTP behavior

Add GET /orders/{orderId:guid}/shipment, available in both the default and Development environments.

- Require bearer authentication; anonymous requests return 401.
- Filter ownership through Orders.CustomerId; missing or nonowned orders/shipments return 404.
- Return 404 while an owned order has no shipment yet; the client can retry after asynchronous consumption.
- Return 200 with id, orderId, status, and createdAt once the shipment exists.
- Use Cache-Control: no-store, current SQL state, cancellation-aware queries, and UTC timestamps with a Z suffix.
- Follow the existing read endpoints: no order/payment write rate policy.

Order status remains Paid. Inventory remains reserved by checkout. Refund creation/outcomes keep their existing behavior and do not change the Pending shipment record.

## Verification

Use real SQL Server for transaction and concurrency checks. Cover one valid event, identical-message replay, different-event replay for the same order, concurrent deliveries, malformed/mismatched IDs, ineligible orders/payments, and forced processed-marker failure. Assert exactly one shipment per order, expected marker counts, complete rollback on failures, and unchanged order/payment/stock state.

Extend the existing isolated RabbitMQ verification arrangement, which stops the API while broker checks run. Create a paid fixture order and publish its OrderPaid event before starting the verification consumer. Confirm there is initially no shipment, start the worker, wait for creation, then redeliver the event and confirm exactly one shipment. This exercises consumer downtime without adding a production pause toggle.

Add an HTTP verification script against the normal full stack. In Development it can use the existing payment simulator to produce OrderPaid, poll the owner’s shipment endpoint, and check response fields, privacy headers, missing/other-customer 404 behavior, and authentication. Restore the default API environment after verification. Use isolated fixture accounts and products and document retained test data.

Audit older verification fixtures that used OrderPaid with empty payloads as generic events. Give those fixtures valid paid-order data or use an appropriate generic event type so their original retry/fault-injection checks stay meaningful. Run the full unit suite and relevant SQL, RabbitMQ, and HTTP regression checks. Add the new checks to CI in the correct API-running/stopped phases.

## Files and documentation

Expected changes include Models/Shipment.cs, Data/ShopDb.cs, the EF migration/snapshot, a shipment response DTO and endpoint mapping, the targeted EventConsumer branch, verification runners/scripts, editable HTTP examples, and CI. Update the root README and architecture, workflow, RabbitMQ, API, demo, and verification guides to show the new visible consumer effect and eventual shipment availability.

Preserve the existing uncommitted features in the main checkout. No changes to payment/refund pricing, stock accounting, or public simulation availability are part of this feature.

## Review and implementation handoff

After review of this specification, write a concrete implementation plan with test-first steps, migration handling, and commands for SQL/RabbitMQ/HTTP checks. The user then reviews that plan and selects execution before product code is changed.
