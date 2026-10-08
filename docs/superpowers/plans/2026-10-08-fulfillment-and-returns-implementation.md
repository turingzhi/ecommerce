# Fulfillment and Returns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver all four approved additions: admin shipment listing, atomic shipment history, owner tracking, and whole-order returns connected to existing refunds.

**Architecture:** Extend the current API/ShopDb. ShipmentQueryService handles fulfillment reads; ShipmentService and ShipmentEventHandler append history atomically. ReturnService manages physical return states and delegates financial creation to existing RefundService; ReturnQueryService reads live accounting.

**Tech Stack:** .NET 10, EF Core/SQL Server 10.0.12, existing Identity bearer tokens, xUnit, Python HTTP checks, Docker Compose and RabbitMQ. No new dependency.

**Spec:** [Approved fulfillment and returns design](../specs/2026-10-08-fulfillment-and-returns-design.md).

## Global Constraints

- Work in /Users/kaizhi/Documents/dot-beginner/Ecommerce, where the preceding accepted features exist. Preserve all current uncommitted changes. The chat worktree has an older baseline.
- No payment provider or carrier integration is introduced; financial outcomes continue to use the project's existing Development simulator.
- Returns cover the whole order, once per order; customers supply a reason; the existing order/payment stay Paid/Succeeded; recording receipt does not automatically replenish stock.
- ShipmentAdmin uses permission=shipments:manage; ReturnAdmin uses permission=returns:manage. Existing shipment permissions do not silently gain refund authority.
- Listing defaults: page 1, pageSize 20, maximum 50, minima 1. Offsets above int.MaxValue yield empty pages. Stable newest-first CreatedAt/Id ordering.
- Customer tracking uses RepeatableRead; never include admin identities. Private reads/writes use no-store and UTC Z timestamps.
- Return writes acquire order → payment → return locks (UPDLOCK/HOLDLOCK), resolving immutable relationships first and rechecking afterward. Never acquire an earlier lock while holding a return write lock.
- History begins after upgrade; preserve old status/tracking/timestamps without fabricated actors or backfill. Returns are not automatically created for old orders.
- Completed requires Succeeded refunds equal the original payment and no Pending/Unknown attempt. Pending refunds do not complete physical returns.
- RefundService remains authoritative for financial caps, unresolved attempts and idempotency. No nested refund transaction or second accounting system.
- Record pre-feature tracked and untracked contents in this plan's ignored execution workspace. Stage only new feature files/hunks if a coherent commit can be separated; otherwise defer mixed commits and keep the ledger. Never stage the whole checkout.
- Use ECOMMERCE_SQLSERVER as documented; do not print credentials. GUID-named verification databases are cleaned in finally. Restore default API after Development/broker checks; never delete application data.

## Review Focus

1. A new status becomes visible while its history is still being read: customer current state and timeline must agree (Task 3).
2. Existing Delivered shipments without history: preserve old fulfillment and permit eligible returns without invented audit records (Tasks 2/4).
3. Retrying return creation after full refund/completion: replay before mutable eligibility checks (Task 4).
4. A shipment-only administrator attempts financial return routes: 403, including for known IDs (Task 5).
5. Completion racing with owner/admin refunds or financial outcomes: no excess refund, false completion, or inverted lock order (Task 5).

## File responsibilities

- Models/ShipmentHistory.cs, Models/ReturnRequest.cs, Data/ShopDb.cs, generated migrations/snapshot: schema constraints and read indexes.
- Services/PageBounds.cs: shared bounded paging arithmetic.
- Services/ShipmentQueryService.cs and Services/ReturnQueryService.cs: current private reads, stable lists and live financial totals.
- Services/ShipmentService.cs and Services/ShipmentEventHandler.cs: history within existing business transactions.
- Services/ReturnService.cs: return eligibility, replay and monotonic physical states; existing RefundService handles financial writes.
- Services/ReturnAdministration.cs and Services/AdminPermissionGrant.cs: separate claim policy and reusable CLI grant implementation; preserve ShipmentAdministration's public constants/signature.
- Dtos: distinct customer/admin history, tracking/list/return DTOs and request bodies. Customer history type has no ActorId property.
- Endpoints/ShipmentEndpoints.cs and Endpoints/ReturnEndpoints.cs: authenticated routes, status codes and policies.
- Verification/ShipmentHistoryVerification.cs, Verification/ShipmentReadVerification.cs, Verification/ReturnVerification.cs, Verification/ReturnConcurrencyVerification.cs: real SQL behavior in isolated databases.
- scripts/verify_fulfillment_http.py and scripts/verify_returns_http.py, Http/shipments.http, Http/returns.http: API regressions and editable walkthroughs.
- Program.cs, CI, README and detailed guides: registration, CLI/verifier dispatch, correct test phases and user instructions.

## Task 1: Admin shipment list

**Create:** Services/PageBounds.cs, Services/ShipmentQueryService.cs, Dtos/ShipmentListResponse.cs, Verification/ShipmentReadVerification.cs, Ecommerce.Tests/PageBoundsTests.cs, scripts/verify_fulfillment_http.py.
**Modify:** Endpoints/ShipmentEndpoints.cs, Program.cs, Verification/ShipmentVerification.cs, docs/api.md, Http/shipments.http.

**Interfaces:**
- PageBounds(int Page, int PageSize, long Offset), static Normalize(int? page, int? pageSize) with default size 20.
- ShipmentListResponse(int Page, int PageSize, IReadOnlyList<ShipmentResponse> Shipments).
- ShipmentQueryService(ShopDb db).ListAsync(string? status, int? page, int? pageSize, CancellationToken): Task<ShipmentListResponse>.
- VerifyShipmentReadsAsync(): private static Task, invoked by RunShipments; creates/migrates/deletes its own GUID-named read-verification database in finally. VerifyShipmentListsAsync(DbContextOptions<ShopDb> options): private static Task called there, so fixed-fixture ordering is independent of the other shipment scenarios.
- verify_fulfillment_http.py default mode validates admin lists/permissions; --development will gain paid/history scenarios in Tasks 2/3.

- [ ] Write PageBoundsTests.PagingNormalizesAndAvoidsOverflow with literal assertions:

```csharp
Assert.Equal(new PageBounds(1, 20, 0), PageBounds.Normalize(null, null));
Assert.Equal(new PageBounds(1, 1, 0), PageBounds.Normalize(0, 0));
Assert.Equal(50, PageBounds.Normalize(1, 1000).PageSize);
Assert.Equal(107374182300L, PageBounds.Normalize(int.MaxValue, 50).Offset);
```

Normalization cases: null→(1,20,0), (0,0)→(1,1,0), size1000→50, (int.MaxValue,50)→offset107374182300. Run dotnet test; observe missing interface/behavior before implementation.
- [ ] Write SQL list checks in Task1's dedicated read database with fixed distinct fixture timestamps and tied timestamps/IDs. Assert newest-first order, disjoint pageSize1 pages, status-only matches, size cap, empty far-out page, and unchanged shipment count/status. Run --verify-shipments against the original reader before implementing.
- [ ] Implement PageBounds and ShipmentQueryService.ListAsync. Build AsNoTracking query; normalize bounds; calculate long offset; skip only when within int range; order CreatedAt/Id descending; map via ShipmentResponse.From after materialization.
- [ ] Add GET /admin/shipments with ShipmentAdmin, 400 for unknown case-sensitive status, no-store/cancellation and no customer write policy. Register query service. HTTP RED: anonymous route currently returns404 rather than401; then rebuild and verify401/403, all three filters, bounds/overflow, 400 unknown/malformed query and repeated reads without write quota. Use real data comparison, not source assertions.
- [ ] Run dotnet test and --verify-shipments, then default HTTP list checks. Update API/example docs and record a scoped checkpoint.

## Task 2: Atomic shipment history

**Create:** Models/ShipmentHistory.cs, Verification/ShipmentHistoryVerification.cs, Dtos/ShipmentHistoryResponse.cs.
**Modify:** Data/ShopDb.cs, ShipmentService.cs, ShipmentEventHandler.cs, ShipmentQueryService.cs, ShipmentEndpoints.cs, existing direct-service verification call sites, ShipmentRabbitMqVerification.cs and migrations/snapshot.

**Interfaces:**
- ShipmentHistory: long Id (identity PK), Guid ShipmentId, string? FromStatus, string ToStatus, DateTime OccurredAt, string? ActorId (maximum450). Restrictive shipment FK; index ShipmentId/Id.
- ShipmentService.UpdateStatusAsync(Guid shipmentId, UpdateShipmentStatus request, string actorId, CancellationToken cancellationToken=default): Task<ShipmentUpdateResult>. Replace the old overload; API supplies NameIdentifier, internal fixtures use a fixed verification actor.
- ShipmentHistoryEntryResponse(long Id, string? FromStatus, string ToStatus, DateTime OccurredAt, string? ActorId), From(ShipmentHistory) normalizes UTC.
- ShipmentHistoryResponse(Guid ShipmentId, IReadOnlyList<ShipmentHistoryEntryResponse> History).
- ShipmentQueryService.GetHistoryAsync(Guid shipmentId, CancellationToken): Task<ShipmentHistoryResponse?>.
- VerifyShipmentHistoryAsync(options) and VerifyShipmentHistoryRollbackAsync(options): private static Task, invoked by RunShipments.

- [ ] Write real SQL tests before history code: initial valid paid event produces [Pending] with null actor; ship by actor-A then deliver by actor-B produces [Pending,Shipped,Delivered] with exact FromStatus and actors. Same/new-ID paid replay and equivalent status replay keep IDs/timestamps/count. Concurrent same/conflicting tracking and delivery yield exactly one entry per committed transition. Observe failure before implementation.
- [ ] Implement mapping and generate AddShipmentHistory using installed EF10.0.12. Review Up/Down: add only history/index/FK; no existing-row data writes. Require nonempty actor before admin changes; obtain it from authentication, not body fields. Update verifier wrappers to the new signature.
- [ ] Insert initial history only when creating a new shipment inside ShipmentEventHandler's existing transaction. For status changes, add history inside the existing shipment-row transaction before SaveChanges; rejected/replayed updates add none. Record OccurredAt using the same timestamp assigned to the transition.
- [ ] Pin VerifyShipmentHistoryAsync to real materialized rows before implementation:

```csharp
Check(rows.Select(h => h.ToStatus).SequenceEqual(new[] { "Pending", "Shipped", "Delivered" }), "History contains committed forward states");
Check(rows[0].ActorId is null && rows[1].ActorId == "actor-A" && rows[2].ActorId == "actor-B", "History records system and authenticated actors");
Check(rows.Count == 3 && rows[1].FromStatus == "Pending" && rows[2].FromStatus == "Shipped", "Replay creates no extra history");
```

- [ ] Inject a CHECK(1=0) history constraint in the isolated database. Assert initial delivery failure leaves no shipment/marker/history; a shipping failure leaves Pending, null shipping details and its original initial entry. Remove in finally and prove retry. Add historical Delivered fixture with no entries; query returns empty history without modifying status.
- [ ] Implement ordered history read (Id ascending) and GET /admin/shipments/{shipmentId:guid}/history. Check existence separately, use ShipmentAdmin/no-store, 404 unknown,401/403 denied. Add UTC/null actor serialization tests and HTTP spoofed actorId test proving the recorded ID comes from authentication.
- [ ] Extend real broker verification to assert initial history count1 after delayed publication/replays. Run unit/--verify-shipments; broker checks run in Task6's paused-API phase. Record checkpoint and update shipment guide.

## Task 3: Private customer tracking

**Create:** Dtos/OrderTrackingResponse.cs and Ecommerce.Tests/TrackingResponseTests.cs.
**Modify:** ShipmentQueryService.cs, ShipmentEndpoints.cs, ShipmentReadVerification.cs, verify_fulfillment_http.py, API/workflow/example docs.

**Interfaces:**
- CustomerShipmentHistoryEntry(string? FromStatus, string ToStatus, DateTime OccurredAt), no actor/ID properties.
- OrderTrackingResponse(Guid OrderId, string OrderStatus, ShipmentResponse? Shipment, IReadOnlyList<CustomerShipmentHistoryEntry> History).
- ShipmentQueryService.GetTrackingAsync(string customerId, Guid orderId, CancellationToken): Task<OrderTrackingResponse?>.
- VerifyCustomerTrackingAsync(DbContextOptions<ShopDb> options): private static Task invoked by Task1's VerifyShipmentReadsAsync after list assertions. That orchestrator is called by RunShipments.

- [ ] SQL RED: owner of unpaid fixture gets tracking with matching orderId/orderStatus, null shipment, empty history; nonowner/unknown returns null. Delivered fixture returns current details and [Pending,Shipped,Delivered] ordered by historyId. JSON test asserts literal Z timestamp and absence of actorId/admin identity.
- [ ] Implement RepeatableRead across ownership, shipment and history reads; use AsNoTracking, map only the customer history type, commit/dispose read transaction. Return null for unknown/nonowned; return200 tracking-with-null-shipment for owned unfulfilled order. Keep old /shipment404 behavior.
- [ ] Add a deterministic concurrency test using SQL command interceptors/barriers only in verification utilities: hold reader after shipment read, start a writer, release reader, and assert each response's last history status matches its shipment status. Bound barriers/timeouts and clean up in finally; no test hooks in production services.
- [ ] Add GET /orders/{orderId:guid}/tracking with authorization, server customer identity, cancellation, no-store, no write policy. HTTP RED before route; then check401, nonowner404, missing404, pre-consumption200, current tracking during updates and hidden actors. Verify25 reads followed by an order/payment write still succeeds.
- [ ] Run unit/--verify-shipments and Development fulfillment HTTP; update docs and checkpoint.

## Task 4: Return storage and owner request/replay

**Create:** Models/ReturnRequest.cs, Dtos/CreateReturn.cs, Dtos/ReturnResponse.cs, Services/ReturnService.cs, Services/ReturnQueryService.cs, Endpoints/ReturnEndpoints.cs, Verification/ReturnVerification.cs, Ecommerce.Tests/ReturnResponseTests.cs, scripts/verify_returns_http.py, Http/returns.http.
**Modify:** Data/ShopDb.cs, Program.cs, migration/snapshot and API/workflow/docs index.

**Interfaces:**
- ShopDb.ReturnRequests: DbSet<ReturnRequest>; ShopDb.ShipmentHistory: DbSet<ShipmentHistory>.
- ReturnRequest fields exactly as spec: Guid Id/OrderId/PaymentId; string IdempotencyKey(max100), Reason(max500), Status="Requested"; UTC CreatedAt; nullable ApprovedAt/ReceivedAt/CompletedAt. Unique OrderId; restrictive order/payment FKs; Status/CreatedAt/Id list index with descending dates/IDs.
- CreateReturn(string? Reason).
- ReturnError {InvalidRequest,NotFound,Conflict}; ReturnResult(ReturnRequest? Return, ReturnError? Error=null, string? Detail=null, bool Replayed=false).
- ReturnService(ShopDb db, RefundService refunds).CreateAsync(string customerId, Guid orderId, string? key, CreateReturn request, CancellationToken): Task<ReturnResult>.
- ReturnResponse fields: Id, OrderId, PaymentId, Reason, Status, CreatedAt, ApprovedAt, ReceivedAt, CompletedAt, Currency, OriginalAmountCents, RefundedCents, ReservedRefundCents, RemainingRefundableCents. Static From(ReturnRequest,Payment,long refundedCents,long reservedRefundCents) normalizes nullable/required UTC timestamps and computes remaining=original-refunded-reserved.
- ReturnQueryService(ShopDb db).GetForOwnerAsync(string customerId, Guid orderId, CancellationToken): Task<ReturnResponse?>; GetByIdAsync(Guid returnId,CancellationToken): Task<ReturnResponse?> for authorized handlers.
- Verification.RunReturns(): public static Task for --verify-returns, GUID database migrated/deleted in finally; reuse paid/shipped fixture factory from Verification partial and authenticated-actor service signature.

- [ ] Write schema/owner creation tests first: Delivered Paid fixture creates one Requested return; trimmed reason stored; key/reason replay sameID; changed reason or different key conflicts; empty/501-char reason and missing/101-char key invalid; nonowner/unknown404; Pending/Shipped/unpaid/fully-refunded orders conflict. At valid boundaries500-char reason/100-char key succeeds. Run RED before implementation.
- [ ] Generate AddOrderReturns with only new table/FKs/indexes. Implement CreateAsync locking order → matching successful payment → existing return. Check existing key/normalized reason before mutable eligibility so completed/fully-refunded replays work; validate ownership and stable relationship IDs under locks. Check Delivered and some charge not successfully refunded. Payment/return identities come from SQL.
- [ ] Implement live DTO reads in a consistent transaction, joining PaymentId and matching Payment.OrderId to the return order, locking/reading accounting in payment-before-return order; aggregate Succeeded separately from Pending/Unknown, exclude Failed. Do not persist duplicated totals. Owner filtering happens before exposing return data; avoid holding return lock then requesting an earlier payment lock.
- [ ] Add owner POST /orders/{orderId:guid}/returns and GET /orders/{orderId:guid}/return. POST uses OrderPolicy and returns201 first,200 replay,400 invalid,404 nonowned/missing,409 conflict; read is no-store/no write quota. Map/register services and --verify-returns before API startup.
- [ ] Add insert-failure rollback and concurrent same/different-key tests in independent contexts: one return; same key replays once; competing keys one409; no stock/order/payment changes. Include Delivered historical shipment without history and later full-refund creation replay. UTC JSON covers SQL DateTimeKind.Unspecified and null transition times.
- [ ] Run unit/--verify-returns/--verify-shipments; HTTP owner creation/auth/eligibility checks; docs/checkpoint.

## Task 5: Return administration and refund completion

**Create:** Services/ReturnAdministration.cs, Services/AdminPermissionGrant.cs, Dtos/UpdateReturnStatus.cs, Dtos/ReturnListResponse.cs, Verification/ReturnConcurrencyVerification.cs.
**Modify:** ShipmentAdministration.cs, ReturnService.cs, ReturnQueryService.cs, ReturnEndpoints.cs, Program.cs, ReturnVerification.cs, verify_returns_http.py and Docker/return/API guides.

**Interfaces:**
- AdminPermissionGrant.GrantAsync(UserManager<IdentityUser> users,string email,string permission): static Task; existing-account-only, idempotent sequential grant. ShipmentAdministration.GrantAsync keeps its signature and delegates; ReturnAdministration has Policy="ReturnAdmin", Permission="returns:manage", GrantAsync(users,email).
- UpdateReturnStatus(string? Status); ReturnListResponse(int Page,int PageSize,IReadOnlyList<ReturnResponse> Returns).
- ReturnQueryService.ListAsync(string? status,int? page,int? pageSize,CancellationToken): Task<ReturnListResponse>, Task1 PageBounds, validated status, stable newest-first query/live totals.
- ReturnService.UpdateStatusAsync(Guid returnId,UpdateReturnStatus request,CancellationToken): Task<ReturnResult>.
- ReturnService.CreateRefundAsync(Guid returnId,long amountCents,string? key,CancellationToken): Task<RefundResult>, consumes existing RefundService.Create(Guid,long,string). Resolve Received return/payment without holding a surrounding transaction, then delegate financial validation/locking. Map absent return to404 and wrong stage to409; bad amount/key400 at endpoint.
- VerifyReturnCompletionRacesAsync(options) and VerifyReturnAdministrationAsync(options): private static Task called by RunReturns.

- [ ] HTTP RED for /admin/returns and admin status/refund routes; create separate shipment-only and return-only operator fixtures. Assert401 anonymous,403 customer/wrong permission (including known IDs), and no public grant route. Test missing account, repeat grants, old token403 and fresh-login privilege. Add --grant-return-admin EMAIL with exact CLI argument validation; preserve shipment grant behavior.
- [ ] Implement list with filters Requested/Approved/Received/Completed, default20/cap50/min1/long-offset protection; unknown filter400. Test tied timestamps, status matches, disjoint pages and overflow in SQL/HTTP; no-store on all private responses.
- [ ] SQL RED forward state machine: Requested→Approved→Received; each equivalent replay preserves timestamps, skip/backward409, unknown400, missing404. Implement shared order→payment→return locking/rechecks on every status write. Requested target always conflicts, even on Requested (no reset operation). Do not change inventory or financial states.
- [ ] Implement admin POST refund via existing RefundService with user key/amount. Test beforeReceived409, invalid amount/key400, cap rejection, same-key replay/changed amount conflict, partial success, failed attempt/new-key retry, Unknown unresolved blocking and resolution. Use owner simulator for outcomes; no admin/default simulator is added.
- [ ] VerifyReturnCompletionRacesAsync uses independent contexts and literal accounting assertions after all tasks settle:

```csharp
Check(await db.ReturnRequests.CountAsync(r => r.OrderId == fixture.OrderId) == 1, "Return remains unique under races");
Check(succeededCents + reservedCents <= 5000, "Concurrent owner/admin refunds cannot exceed the fixture payment");
Check(stored.Status != "Completed" || succeededCents == 5000 && reservedCents == 0, "Completed implies fully settled original charge");
```

- [ ] SQL RED completion requires total successful original amount and no unresolved attempt; assert Pending/Unknown/partial/Failed refunds cannot complete, then settle remaining balance and complete once. Repeat Completed preserves time. Creation replay after completion still returns the original Requested-record identity with current Completed status.
- [ ] Race tests (five iterations each): concurrent return creation, two identical completion calls, completion versus full refund success, owner refund versus admin refund, and completion versus new refund creation. Assert one stored return, no excess reserved/refunded total, legal completion result/state, stable times, no deadlock (bounded timeout). Read current financial totals and unchanged stock. Forced return-save CHECK failure rolls back status/time and permits retry after removal.
- [ ] Run full unit, --verify-returns and shipment regressions; complete Development HTTP flow and default auth/list checks; checkpoint.

## Task 6: Integrated verification, documentation and review

**Modify:** .github/workflows/ecommerce.yml, README.md, docs/README.md, api.md, workflows.md, architecture.md, verification.md, docker.md, shipments.md; create docs/returns.md; finalize editable shipment/return HTTP examples and both new scripts.

- [ ] Add CI --verify-returns alongside SQL checks; default fulfillment/return permission checks with API running; full Development flows before restoring default; extend existing paused-API broker phase for shipment-history replay. Do not run competing verification consumers with the API active.
- [ ] Rewrite README capability and endpoint tables with all new routes. Document separate grant commands, fresh login, whole-order/once-only return policy, no automatic restock, successful-full-refund completion, pending/Unknown behavior, history starting after upgrade, privacy and retained fixture accounts/data.
- [ ] Final verification: dotnet build Ecommerce.sln --no-restore; dotnet test Ecommerce.sln --no-build --no-restore; with ECOMMERCE_SQLSERVER, --verify, --verify-sqlserver, --verify-shipments, --verify-returns. Expected: no failed checks, isolated database cleanup, existing warnings only.
- [ ] Rebuild Development API; run verify_fulfillment_http.py --development, verify_returns_http.py --development, existing shipment/status/refund/financial-read scripts. New flows exercise no-history historical fixtures, hidden actor data, live accounting, refund failure/Unknown resolution, all state replays and both permissions.
- [ ] Drain live/retry broker queues, stop API, run --verify-rabbitmq and --verify-product-sync, always restore API in finally. Restore default environment and run both new default scripts, existing shipment/status, checkout/health and simulator-disabled checks. Record exactly which fixtures are retained. Check all five containers healthy and git diff --check.
- [ ] Request one fresh independent review against feature-only baseline/spec, focused on the five Review Focus conditions. Fix blocking findings with reproducing RED→GREEN tests and rerun affected/full suites; preserve prior changes. Record final results and scoped commits only where separation is safe.

## Execution handoff

Native execution is recommended: these six tasks share schema, query and lock-order interfaces and form one dependency chain. Implement in this chat with a ledger/checkpoints, then one fresh independent reviewer. Subagent-driven execution is available if separate per-task implementer/reviewer gates are preferred. Start product changes only after the user reviews this plan and chooses the execution method.
