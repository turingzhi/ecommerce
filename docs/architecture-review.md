# DI, database, and Redis review

[Documentation index](README.md) · [Architecture](architecture.md) · [Learning guide](knowledge/README.md)

Reviewed on 9 October 2026 against the current source. This was a source review,
including existing test and verification code. Application tests, load tests,
database queries, and outage exercises were not run. Application code was not
changed. Examples below are proposed changes, not implemented fixes.

## Overall assessment

The DI lifetimes fit this application. Database services are scoped, the Redis
multiplexer is shared, and hosted workers create scopes for database work. There
is no reason to add interfaces to every service or introduce a repository layer.

The main correctness issue is unchecked payment-total arithmetic. There are also
an inefficient returns list, an order-pagination overflow, and gaps in cancellation
and verification. Inconsistent stock lock ordering creates a deadlock risk. No
Critical finding was identified in this review's scope; this is not a complete
security audit or a production-readiness certification.

## DI and lifetime inventory

All explicit application registrations are in [Program.cs](../src/Ecommerce.Api/Program.cs).

| Lifetime | Registrations | Lines |
| --- | --- | --- |
| Scoped | `DefaultAdminBootstrapper`, `ShopDbContext` | 65, 84–85 |
| Scoped | `OrderService`, `OrderQueryService`, `PaymentQueryService`, `CartService`, `ProductCatalogService`, `ProductQueryService`, `PaymentService`, `RefundService`, `ReturnService`, `ReturnQueryService`, `ShipmentService`, `ShipmentQueryService` | 86–97 |
| Scoped | `IEventPublisher` → `RabbitMqEventPublisher`, `EventConsumer`, `OutboxDispatcher`, `ProductSearchService` | 101–103, 117 |
| Singleton | `OrderExpirationWorker`, `OutboxWorker`, `RabbitMqConsumerWorker`, registered through `AddHostedService` | 98, 104–105 |
| Singleton | `ElasticsearchClient`, `IConnectionMultiplexer` → Redis `ConnectionMultiplexer` | 109–121 |
| Transient | No explicit application `AddTransient` registrations | — |

Identity, MVC, authorization, rate limiting, telemetry, options, and health checks
also register framework services. The table describes the application's explicit
registrations, not every framework descriptor.

No unintended duplicate application registration was found. The three hosted
services intentionally register under `IHostedService`; the host runs all of them.
For ordinary unkeyed registrations, resolving one service selects the last
registration, while `IEnumerable<T>` returns all in registration order. There are
no keyed registrations here. See [Microsoft's registration rules](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection#service-registration-methods).

`IEventPublisher` is a useful boundary: verification code can substitute a publisher.
Concrete business services are also valid DI dependencies. `[FromBody]` binds HTTP
data; `[FromServices]` resolves a registered service. Neither requires introducing
an interface. See [the DI learning chapter](knowledge/csharp-fundamentals.md#interfaces-and-dependency-injection).

## Confirmed findings

### 1. Payment totals can overflow — Medium

**Location:** [OrderRules.cs](../src/Ecommerce.Api/Features/Orders/Services/OrderRules.cs),
lines 25–31; [ProductCatalogService.cs](../src/Ecommerce.Api/Features/Catalog/Services/ProductCatalogService.cs),
lines 88–94; [PaymentService.cs](../src/Ecommerce.Api/Features/Payments/Services/PaymentService.cs),
lines 63–82 and 119–125.

Catalog validation accepts any nonnegative `long` price. The total multiplies and
adds without overflow checking. A product priced at `long.MaxValue` cents with
quantity 2 produces −2 cents in the current unchecked arithmetic. Payment creation
saves that amount, and the simulated success path can mark the order paid.
This requires an extreme catalog price, but it violates monetary integrity.

**Recommended fix:** check multiplication and addition, validate the total before
committing an order, and return a business error on overflow. Set a sensible catalog
price limit. A defensive payment-amount constraint can supplement these checks;
decide whether zero-value orders are supported before choosing `>= 0` or `> 0`.

```csharp
// Proposed arithmetic inside CalculateTotalCents.
long total = 0;
foreach (var item in savedItems)
    total = checked(total + checked(item.UnitPriceCents * item.Quantity));
```

`checked` throws rather than wrapping; the caller must translate that error and
avoid committing an unpayable order. See [C# overflow checking](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/checked-and-unchecked).
Existing tests cover values above `int.MaxValue`, but not `long` overflow.

### 2. Returns listing repeats queries per item — Medium

**Location:** [ReturnQueryService.cs](../src/Ecommerce.Api/Features/Returns/Services/ReturnQueryService.cs),
lines 14–35.

`ListAsync` loads IDs, then calls `GetByIdAsync` for each. Each normal detail call
executes four data queries and opens a transaction. Ten returns therefore require
41 data queries, plus transaction commands. It also loads all refund rows to sum
them in memory. This increases latency and repeatedly locks payment rows.

**Recommended fix:** fetch the page with payment data and grouped refund totals
in a bounded number of queries. Preserve the intended consistency of status and
balances; do not replace the loop with `Task.WhenAll` on the same context.
Measure query count and latency before and after. See [EF query guidance](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying#load-related-entities-eagerly-when-possible).

### 3. Customer order pagination can overflow — Low

**Location:** [OrdersController.cs](../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs),
lines 82–95; existing safe helper: [PageBounds.cs](../src/Ecommerce.Api/Common/Pagination/PageBounds.cs),
lines 4–8.

The offset uses `int` multiplication. `page=2147483647&pageSize=50` wraps the offset
to −100. This can produce a database error; other overflows can select the wrong
page. The owner filter still applies, so this is not an ownership bypass.

**Recommended fix:** use `PageBounds.Normalize`, calculate in `long`, and handle
offsets above `int.MaxValue` before converting to `int`, as other query paths do.
Preserve the route's current default page size of 10; the helper defaults to 20.

### 4. Expiration cannot cancel an in-flight SQL operation — Low

**Location:** [OrderExpirationWorker.cs](../src/Ecommerce.Api/Features/Orders/Services/OrderExpirationWorker.cs),
lines 74–85; [OrderService.cs](../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs),
lines 167–249.

The worker checks its stopping token between orders, but `Expire` accepts no token.
A shutdown cannot cancel that order's database lock wait through this path. Its
scope remains alive until SQL work finishes or fails. HTTP order/payment/refund
operations also have incomplete cancellation coverage.

**Recommended fix:** pass an optional token through `Expire` and its EF operations,
then cover cancellation during blocked SQL work. Extend other paths incrementally.
Cancellation never proves an already committed operation failed; retain idempotent
retries for uncertain client outcomes.

### 5. Search verification waits for an unrelated global signal — Low

**Location:** [verify_search_http.py](../tests/http/verify_search_http.py), lines 91–107.

The script treats any generation change as completion of its product update.
Another product event can increment the global counter first, causing the freshness
assertion to run too early. This is a source-backed flaky-test risk; it was not
reproduced during this review.

**Recommended fix:** wait for the expected updated result or the update's own SQL
processed marker, then check repeat freshness. A global generation change alone
does not identify which event finished.

## Risks and optional improvements

### 6. Stock lock ordering can deadlock — Medium risk

**Location:** [OrderService.cs](../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs),
lines 57–69, 137–146, and 214–227; [Program.cs](../src/Ecommerce.Api/Program.cs), lines 84–85.

Checkout changes stock in request order; cancellation and expiration use unsorted
loaded items. Transactions accessing `[P1, P2]` and `[P2, P1]` can each hold one
product lock and wait for the other. SQL Server can abort a victim, producing a
failed request. The transaction rolls back: this is not evidence of overselling.
The inconsistent ordering is confirmed in source; no deadlock trace was captured.

**Recommended fix:** order all three stock loops by `ProductId`. Add an opposite-order
concurrency scenario. If residual transient failures justify retries, retry the
whole transaction with a fresh context and the same idempotency key. Simply adding
`EnableRetryOnFailure` around these existing explicit transactions is insufficient;
follow [EF's transaction/retry rules](https://learn.microsoft.com/en-us/ef/core/saving/transactions#controlling-transactions).

### 7. Redis outages exhaust valid product-event retries — Medium limitation

**Location:** [EventConsumer.cs](../src/Ecommerce.Api/Infrastructure/Messaging/EventConsumer.cs),
lines 39–55; [RabbitMqConsumerWorker.cs](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqConsumerWorker.cs),
lines 95–101.

The consumer indexes Elasticsearch, increments Redis, then saves its SQL marker.
Redis connection/timeouts use the counted failure policy; five failed attempts can
send a valid event to the dead queue. Restoring Redis does not automatically replay
that queue. This is already documented. Elasticsearch may already be current, and
cached responses expire after 30 seconds; this does not imply permanent stale data.

**Recommended fix:** treat Redis connection/timeouts as retryable infrastructure
failures while keeping malformed payloads and invalid Redis values bounded. Add an
outage/recovery check and document replay. See [Redis failure behavior](redis.md#failure-behavior-and-limits).

### 8. Publisher connections are recreated per message — Low improvement

**Location:** [RabbitMqEventPublisher.cs](../src/Ecommerce.Api/Infrastructure/Messaging/RabbitMq/RabbitMqEventPublisher.cs),
lines 21–26; [OutboxDispatcher.cs](../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxDispatcher.cs),
lines 16–30.

A full 100-message batch can create 100 connections/channels and repeat topology
declaration. Reuse across a batch or manage a long-lived connection if publication
load warrants it. Define channel ownership and recovery before sharing resources.
Changing the publisher registration to singleton alone does not change method-local
connection creation. See [RabbitMQ connection guidance](https://www.rabbitmq.com/docs/connections#connection-lifespan).

### 9. Search misses can stampede — Low improvement

**Location:** [ProductsController.cs](../src/Ecommerce.Api/Features/Catalog/Controllers/ProductsController.cs),
lines 51–94.

Concurrent misses for one key independently query Elasticsearch. The 30-second
expiry and generation change can group misses. No incorrect response or measured
capacity problem was established. Measure miss bursts before adding per-key request
coalescing. Preserve the current Redis-failure fallback.

## Safeguards to keep

- Workers use fresh scopes per batch, delivery, or expiring order. No captive
  scoped dependency or concurrent production use of one context was found.
- Read services generally use `AsNoTracking`; mutations track entities or use
  conditional updates inside transactions. `ExecuteUpdate` bypasses EF tracking,
  so future code must not assume tracked product stock updates automatically.
  See [EF bulk-update behavior](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete#change-tracking).
- Order replay uses a unique customer/key index and `UPDLOCK`/`HOLDLOCK`, including
  the absent-key range. Inventory deductions check stock atomically; the schema
  also rejects negative stock. Order, stock, and Outbox writes commit together.
- Payment/cancel/expiration paths serialize on the order row. Refund outcomes and
  amounts serialize on the payment row. Shipment/history/processed-marker writes
  are transactional, with one shipment per order.
- Product details use an EF concurrency token. Details updates do not overwrite
  stock, so inventory not incrementing that token is not a confirmed lost-update bug.
- Redis has one reusable multiplexer, which is its intended usage. Cart services
  remain scoped because they also depend on the context. Lua keeps cart capacity
  and expiry changes atomic. See [StackExchange.Redis usage](https://seredis.dev/Basics.html).
- Cache keys include every validated search parameter, a generation, and a hash.
  Filling an old-generation key cannot replace a new-generation entry. Completed
  cart checkout replay checks SQL before Redis and deliberately preserves the cart.

## Prioritized plan

1. **Correctness:** validate monetary totals before order commit; add multiplication
   and sum boundary tests. Fix customer pagination and cover extreme page values.
2. **Concurrency and recovery:** sort stock operations, verify opposite-order
   checkouts, classify Redis outages consistently, and test recovery. Add cancellation
   during expiration SQL work. Preserve existing idempotency and transaction boundaries.
3. **Query efficiency and verification:** remove returns-list query growth while
   preserving balance consistency; measure query counts. Wait for the actual update
   in search verification. Consider a full DI-composition check to catch future drift.
4. **Only before production scale:** coordinate multi-instance Outbox processing,
   define cart persistence and Redis memory policy, replace payment simulations, and
   establish deployment migrations, credentials, monitoring, and recovery procedures.
   The current Compose stack is explicitly for local development.

Do not add context pooling, repositories, extra service interfaces, RCSI, or SNAPSHOT
just to increase architectural complexity. No measurement here justifies pooling,
and no blocking trace establishes that row-versioned isolation is needed. These
choices also do not remove explicit lock-order or monetary-validation problems.
