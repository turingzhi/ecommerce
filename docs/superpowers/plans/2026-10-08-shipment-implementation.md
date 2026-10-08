# Event-driven Shipment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create exactly one Pending shipment per paid order through RabbitMQ and expose an authenticated owner-only read.

**Architecture:** Extend EventConsumer with a focused ShipmentEventHandler. The handler serializes creation on the order row and commits the shipment and processed-event marker together. Preserve the existing publisher, retry topology, product synchronization, and payment/refund accounting.

**Tech Stack:** .NET 10, EF Core/SQL Server 10.0.12, RabbitMQ.Client 7.2.2, existing xUnit and Python HTTP verification.

**Spec:** [Approved shipment design](../specs/2026-10-08-shipment-design.md).

## Global Constraints

- Each order may have at most one shipment.
- Status: Pending. CreatedAt: UTC creation time.
- Existing Paid orders whose events were already marked processed will not be backfilled.
- The unique OrderId index provides a second database constraint on duplicate creation.
- Order status remains Paid. Inventory remains reserved by checkout.
- Refund creation/outcomes keep their existing behavior and do not change the Pending shipment record.
- Preserve ProductUpserted processing and existing behavior for other commerce events.
- Preserve the existing uncommitted features in the main checkout.
- Execute in /Users/kaizhi/Documents/dot-beginner/Ecommerce, which contains the accepted preceding features. The chat worktree has a different baseline; do not silently switch to it or discard/copy the main checkout's changes.
- Use the installed EF CLI 10.0.12 and current project dependencies. ECOMMERCE_SQLSERVER must be configured as documented in docs/verification.md; do not print secrets.
- Use isolated verification databases and delete only those databases in finally blocks. Restore the API after broker checks and return it to the default environment after Development checks.
- Before code commits, review against a recorded pre-feature diff. Stage only shipment files/hunks; never git add the whole checkout. If a feature-only commit cannot be separated safely from an already modified file, defer that commit and preserve the working changes.

## Review Focus

1. Repeated and concurrent deliveries, including different message IDs for one order: one shipment, correct markers (Task 2).
2. Malformed or inconsistent event identifiers and ineligible SQL state: no shipment or marker (Task 2).
3. Marker insertion failure or cancelled processing: rollback all new effects; retry remains possible (Task 2).
4. Historical processed events at upgrade: remain processed without automatic shipment backfill (Task 2).
5. Customer guessing an order ID, or reading before consumption: 404, with no private shipment data (Task 3).

## File responsibilities

- Models/Shipment.cs and Data/ShopDb.cs: entity, foreign key, unique OrderId index.
- Migrations: EF-generated AddShipmentFulfillment migration/designer and ShopDbModelSnapshot.cs.
- Services/ShipmentEventHandler.cs: SQL eligibility, locking, deduplication, atomic effects.
- Services/EventConsumer.cs: dispatch OrderPaid to that handler; other processing stays intact.
- Dtos/ShipmentResponse.cs and Endpoints/ShipmentEndpoints.cs: owner-only read and UTC response.
- Verification/ShipmentVerification.cs: isolated SQL schema, replay, race, eligibility, and rollback checks.
- Verification/ShipmentRabbitMqVerification.cs: delayed delivery and redelivery against the real broker.
- scripts/verify_shipment_http.py and Http/shipments.http: public API checks and editable examples.
- Program.cs: --verify-shipments dispatch and endpoint mapping.
- Existing RabbitMQ runner, CI, and project guides: fixture compatibility, verification order, observable workflow.

## Task 1: Shipment schema and isolated SQL verification entrypoint

**Files:** Create Models/Shipment.cs and Verification/ShipmentVerification.cs; modify Data/ShopDb.cs and Program.cs; generate Migrations/*_AddShipmentFulfillment.cs, its designer, and update Migrations/ShopDbModelSnapshot.cs.

**Interfaces:**
- Shipment: Guid Id, Guid OrderId, string Status = "Pending", DateTime CreatedAt = DateTime.UtcNow.
- ShopDb.Shipments: DbSet<Shipment>.
- Verification.RunShipments(): public static Task; selected by --verify-shipments before normal API startup.
- Shared verification fixture: private sealed record ShipmentFixture(Guid OrderId, Guid PaymentId, int ProductId, OutboxMessage PaidEvent).
- CreatePaidShipmentFixtureAsync(DbContextOptions<ShopDb> options): private static Task<ShipmentFixture>. Seed an automatically generated product at 5000 cents with stock 2; use existing OrderService and PaymentService to buy one unit and simulate success, then load its OrderPaid Outbox row. Use a fresh context for each operation.
- VerifyShipmentSchemaAsync(DbContextOptions<ShopDb> options): private static Task.

- [ ] Write the schema checks first. In a fresh Verify_ecommerce_shipments_GUID database, migrate and check shipment insertion, duplicate OrderId rejection, nonexistent-order foreign-key rejection, and restrictive order deletion. The factory uses real existing service methods; assertions target SQL results:

```csharp
Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1,
    "Shipment persists for the paid fixture order");
Check(rejectedDuplicateOrder, "Database enforces one shipment per order");
Check(rejectedMissingOrder, "Shipment requires an existing order");
Check(rejectedOrderDeletion, "Shipment foreign key protects its order");
```

- [ ] Run dotnet build. Confirm the new schema test cannot compile before Shipments/model exist, then implement the model and mapping: unique IX_Shipments_OrderId and FK to Orders with DeleteBehavior.Restrict.
- [ ] Generate the migration using the existing EF tool:

```sh
ConnectionStrings__ShopDatabase="$ECOMMERCE_SQLSERVER" dotnet ef migrations add AddShipmentFulfillment --context ShopDb
```

Review the generated Up/Down and snapshot: add/drop only the Shipments table, its primary/foreign keys, and unique OrderId index. No data backfill or changes to existing commerce tables. Use the actual timestamped filenames generated by EF.
- [ ] Add the CLI dispatch and RunShipments isolated-database lifecycle. It migrates its own database, calls the schema checks, and deletes the database in finally.
- [ ] Run dotnet build Ecommerce.sln --no-restore, then dotnet run --no-build -- --verify-shipments. Expect schema PASS checks and successful cleanup. Record a scoped checkpoint/commit under the global staging constraint.

## Task 2: Atomic OrderPaid handler and SQL failure coverage

**Files:** Create Services/ShipmentEventHandler.cs; modify Services/EventConsumer.cs and Verification/ShipmentVerification.cs.

**Interfaces:**
- ShipmentEventHandler(ShopDb db).HandleAsync(OutboxMessage message, CancellationToken cancellationToken): Task<bool>.
- A private typed OrderPaidPayload(Guid OrderId, Guid PaymentId) matches the existing PascalCase payload fields.
- Verification helpers: VerifyShipmentDeliveryAsync(options), VerifyShipmentConcurrentDeliveryAsync(options), VerifyShipmentValidationAsync(options), VerifyShipmentRollbackAsync(options), all private static Task with DbContextOptions<ShopDb>.
- ConsumeShipmentEventAsync(options, message, cancellationToken = default): private static Task<bool>, creating a fresh ShopDb and calling EventConsumer.ConsumeAsync.

- [ ] Write delivery checks before the handler. A real valid PaidEvent currently creates only a marker, so this must fail:

```csharp
Check(await db.Shipments.CountAsync(s => s.OrderId == fixture.OrderId) == 1,
    "OrderPaid creates exactly one shipment");
```

Run --verify-shipments to observe that failure. Add assertions that the shipment is Pending, stock remains 1, order remains Paid, and payment remains Succeeded.
- [ ] Implement HandleAsync and dispatch OrderPaid to new ShipmentEventHandler(db) before the generic event branch. Reject empty event IDs. Check an existing marker before interpreting a historical duplicate. For a new event, validate nonempty envelope/payload IDs and matching OrderId. Begin a SQL transaction; load the order with UPDLOCK, HOLDLOCK; recheck the marker; require Paid and a matching Succeeded payment; insert a shipment only if absent; insert the marker; save and commit. Missing/ineligible state or malformed payload throws JsonException without effects. Propagate cancellation. Return false for processed IDs and true for a newly recorded event, even if the shipment already exists.
- [ ] Add same-ID and different-ID sequential and concurrent checks using independent contexts. Await both consumers; assert one shipment and one marker for same-ID delivery, and one shipment/two event markers for two IDs. Add an already-processed PaidEvent with no shipment and assert false/zero shipments to pin the upgrade behavior.
- [ ] Add invalid JSON, missing/empty IDs, envelope/payload mismatch, missing order/payment, wrong payment order, PendingPayment/Cancelled order, and Pending/Failed payment cases. Each must throw JsonException and leave both tables unchanged. A pre-cancelled token must throw OperationCanceledException and leave no effects.
- [ ] Inject a fixed CHECK (1 = 0) constraint on ProcessedMessages in the isolated database. Processing must throw DbUpdateException; a new context must see zero shipments and zero markers for that fixture. Drop the constraint in finally; retry and check one shipment/one marker. This proves the shipment cannot survive a failed marker commit.
- [ ] Run the complete --verify-shipments runner and dotnet test --no-restore. Expect all checks to pass; inspect that product/other-event paths were preserved. Record a scoped checkpoint/commit.

## Task 3: Owner-only shipment API and HTTP examples

**Files:** Create Dtos/ShipmentResponse.cs, Endpoints/ShipmentEndpoints.cs, Ecommerce.Tests/ShipmentResponseTests.cs, scripts/verify_shipment_http.py, Http/shipments.http; modify Program.cs, docs/api.md, and docs/workflows.md.

**Interfaces:**
- ShipmentResponse(Guid Id, Guid OrderId, string Status, DateTime CreatedAt), static From(Shipment shipment), restoring UTC kind for SQL timestamps.
- MapShipmentEndpoints(this WebApplication app): WebApplication.
- GET /orders/{orderId:guid}/shipment: current owned shipment, 200 DTO; missing/nonowned/no-shipment 404; anonymous 401; no-store; no write rate policy.
- HTTP script default mode: auth, missing/nonowned/no-shipment checks using an unpaid fixture order, then cancel it to restore stock.
- --development mode: create a new paid fixture using the existing payment simulator, poll for a shipment, check positive owner response and other-customer 404. poll_shipment(order_id: str, token: str, timeout_seconds: float = 30) -> dict retries only 404 at 0.5-second intervals; any other unexpected status fails immediately.

- [ ] Write and run the HTTP script against the old image: first anonymous GET must fail with 404 instead of 401. Add tests for a missing UUID, another customer's existing shipment, read before fulfillment, current Pending status, matching order ID, nonempty shipment ID, UTC timestamp, no-store, unchanged stock/order/payment, and repeated reads without consuming the write quota.
- [ ] Implement the endpoint with an AsNoTracking ownership-filtered SQL query through Orders.CustomerId and cancellation token. Map it in Program.cs. Use the existing DTO conventions; do not expose customer IDs or message-processing metadata.
- [ ] Add the UTC response regression for both DateTimeKind.Utc and DateTimeKind.Unspecified:

```csharp
Assert.Equal("2026-10-08T00:00:00Z",
    JsonSerializer.SerializeToElement(response).GetProperty("CreatedAt").GetString());
```

- [ ] Rebuild the Development stack and run python3 scripts/verify_shipment_http.py --development. Expect the payment to become Paid before shipment polling completes, then the Pending shipment to appear. Run the full unit suite. Update API/workflow examples to describe asynchronous availability and use a fresh payment success after upgrade. Record a scoped checkpoint/commit.

## Task 4: Broker recovery, regression checks, CI, and documentation

**Files:** Create Verification/ShipmentRabbitMqVerification.cs; modify Verification/RabbitMqVerification.cs, .github/workflows/ecommerce.yml, README.md, docs/README.md, docs/architecture.md, docs/rabbitmq.md, docs/demo.md, docs/verification.md; create docs/shipments.md.

**Interfaces:**
- VerifyRabbitMqShipmentRecoveryAsync(): private static Task, called after the existing broker scenario stops its worker and cleans up its database, before the overall success message.
- Reuse Task 1's fixture factory, existing RabbitMQ worker/publisher/dispatcher, and existing WaitUntilAsync helper. Create a separate GUID-named database and independent worker/service provider for this scenario.

- [ ] Write the broker scenario before wiring it: create a paid fixture with no consumer running; dispatch its Outbox; assert confirmed publication and zero shipments. Start its worker and wait for one shipment and the paid-event marker. Publish the original event again, then a new-ID copy with the same valid payload; wait for the new marker and assert one shipment and both paid-event markers. This confirms delayed consumption and actual replay without relying on a fixed sleep. Stop/dispose the worker and delete only its verification database in finally.
- [ ] Change the existing generic database-retry fixture from OrderPaid/empty payload to OrderCreated/empty payload so it continues exercising marker failure and recovery. Audit remaining fixtures with rg; valid payment-service OrderPaid events keep their business type and payload.
- [ ] Integrate CI: --verify-shipments runs with SQL checks; Development HTTP shipment checks run with the existing simulator checks; real broker recovery runs inside --verify-rabbitmq after the API is paused; default shipment HTTP checks run against the normal API. Restore default environment as the existing workflow does.
- [ ] Update guides and editable examples with the table/model, atomicity and unique-order constraint, owner endpoint, 404-before-processing behavior, no historical backfill, and unchanged Paid/stock/refund behavior. Include commands and retained-fixture behavior. Describe shipment as a Pending fulfillment record; no carrier or delivery integration is added.
- [ ] Run final verification from the main checkout with ECOMMERCE_SQLSERVER configured:

```sh
dotnet build Ecommerce.sln --no-restore
dotnet test Ecommerce.sln --no-build --no-restore
dotnet run --no-build -- --verify
dotnet run --no-build -- --verify-sqlserver
dotnet run --no-build -- --verify-shipments
```

Run Development shipment/refund/financial-read HTTP checks against the rebuilt API. Let the running consumer drain its queue, stop the API, then run --verify-rabbitmq and --verify-product-sync. Always restart the API in finally, run default shipment HTTP and existing order/payment/health scripts, and check git diff --check. Report any failing check by name; success requires all required checks passing.
- [ ] Request a final independent code review of the feature and its migration, address findings, rerun affected checks, and record a final scoped checkpoint. Preserve all pre-feature changes and report the resulting files and verification evidence.

## Execution handoff

Recommend Native execution: the four tasks form one dependency chain and reuse the same SQL/RabbitMQ interfaces. The primary agent can implement them in this chat and request a final fresh independent review. Subagent-driven execution is available if the user prefers separate implementer/reviewer gates per task. Product implementation starts only after the user reviews this plan and selects the execution method.
