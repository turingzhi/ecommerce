# Data access and concurrency

[Learning index](README.md) · [Documentation index](../README.md)

Learn how EF Core reads and writes data, then how SQL Server protects those writes when requests overlap. Replicas, sharding, and distributed locks are later topics. Snippets are teaching examples unless linked to source code.

**In this project:** EF Core uses SQL Server. Transactions, unique indexes, lock hints, and conditional updates protect checkout and other workflows. Read replicas, sharding, and Redis locks are learning topics; they are not configured here.

These protections do not cover every edge case. The [architecture review](../architecture-review.md)
documents amount overflow, inconsistent stock lock ordering, and query/pagination
issues. Its examples are proposed fixes, not current implementation.

## On this page

- [EF Core and Data Access](#ef-core-and-data-access)
- [Database Indexes](#database-indexes)
- [Transactions and ACID](#transactions-and-acid)
- [Concurrency Control](#concurrency-control)
- [Connection Pool](#connection-pool)
- [Database Migration](#database-migration)
- [Read Replicas](#read-replicas)
- [Partitioning and Sharding](#partitioning-and-sharding)
- [Distributed Locking](#distributed-locking)
- [Project examples](#project-examples)
- [How connection pooling works here](#how-connection-pooling-works-here)
- [SQL Server and SQLite comparison](#sql-server-and-sqlite-comparison)

## EF Core and Data Access

### DbContext

A `DbContext` builds queries, tracks loaded entities, saves changes, and works with transactions. It is a short-lived unit of database work, not a thread-safe shared store.

`AddDbContext` normally registers it as scoped. A request gets its own context,
while collaborating services in that scope can share it sequentially. Scope
disposal cleans up the context; connection pooling separately reuses physical
SQL connections. See [DbContext versus Redis](csharp-fundamentals.md#dbcontext-versus-redis)
for the lifetime comparison and [disposal](csharp-fundamentals.md#disposal-versus-garbage-collection)
for ownership and memory cleanup.

```csharp
var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId);
if (order is not null)
{
    order.Status = "Cancelled";
    await db.SaveChangesAsync();
}
```

This shows tracked persistence only. Real cancellation also checks ownership and payment state and restores stock; see [OrderService.Cancel](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs).

### IQueryable

An `IQueryable<T>` describes a query. Building it does not normally fetch the rows:

```csharp
var query = db.Orders.Where(o => o.CustomerId == customerId);
var orders = await query.ToListAsync(); // Execute and load the results.
```

Operations such as `ToListAsync`, `FirstAsync`, `CountAsync`, and `SingleAsync` execute a query. Once results are in a list, further LINQ runs over those in-memory objects. Filter before loading when possible.

### Projection

Projection selects only the data a caller needs:

```csharp
var summaries = await db.Orders
    .Where(o => o.CustomerId == customerId)
    .Select(o => new { o.Id, o.Status, o.CreatedAt })
    .ToListAsync();
```

Compared with loading complete entities and related data, this can reduce network traffic, memory use, and database work.

### AsNoTracking

Use `AsNoTracking()` when you do not plan to edit loaded entities. EF Core then skips normal change tracking for those results. This reduces overhead; it does not change transaction isolation or lock behavior.

### N+1 Problem

An N+1 query pattern loads a list, then sends another query for each item. Loading 100 orders followed by 100 separate customer lookups means 101 queries.

Use a suitable `Include`, join, projection, or batch query. Choose based on the required data and inspect the resulting SQL; one large join can also return more data than needed.

### Tracked changes versus immediate updates

Changing a tracked property waits for `SaveChangesAsync`. `ExecuteUpdateAsync` sends SQL immediately. It does not refresh already tracked objects or automatically apply their concurrency tokens. Several immediate updates and a later save need an explicit transaction if they must commit together. See [EF Core bulk updates](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete).

Checkout uses this pattern in [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs):

```csharp
var affected = await db.Products
    .Where(p => p.Id == item.ProductId && p.Available >= item.Quantity)
    .ExecuteUpdateAsync(update => update.SetProperty(
        p => p.Available, p => p.Available - item.Quantity));
```

Zero affected rows means the condition failed. The surrounding transaction groups these stock changes with the order and Outbox save.

## Database Indexes

An index helps SQL find or order matching rows without scanning all data. It may support a filter, join, or sort, but SQL chooses whether to use it.

```sql
CREATE INDEX IX_Users_Email ON Users(Email);
```

This is an illustrative index. Every index costs storage and write work. Use execution plans and measurements to decide which indexes help.

### Composite Index

A composite index uses several columns. Their order matters. An index beginning with `(CustomerId, CreatedAt)` can support one customer's time-ordered list, but may not help a query filtering only by `CreatedAt` in the same way.

### Covering Index

An index covers a query when it contains all required columns. SQL may then avoid looking up rows in the base table. This project's order-list index includes `Status` and `Currency`; see the [local measurement](../sqlserver-order-list-performance.md).

## Transactions and ACID

### ACID

| Property | Meaning |
| --- | --- |
| Atomicity | A transaction commits all its changes or none of them |
| Consistency | Valid database rules remain valid when changes commit |
| Isolation | Concurrent transactions interact under defined rules |
| Durability | Committed changes are retained according to the database's storage guarantees |

Transactions do not invent business rules. Define them with checks, constraints, and the right update operations.

### Transaction

A single `SaveChangesAsync` normally saves its changes transactionally. Use an explicit transaction for multiple saves or immediate updates that belong together:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync();
// Perform related database operations.
await db.SaveChangesAsync();
await transaction.CommitAsync();
```

If an exception or early return leaves this transaction uncommitted, disposal rolls it back. A local SQL transaction cannot also atomically publish to RabbitMQ; that is why the project uses an Outbox.

### Isolation levels

The isolation level defines what concurrent transactions may observe:

| Level | Read behavior |
| --- | --- |
| Read uncommitted | May see data that later rolls back |
| Read committed | Avoids dirty reads; repeated statements may see different committed data |
| Repeatable read | Protects rows already read; new matching rows may still appear |
| Serializable | Also protects qualifying key ranges from new matching rows |
| Snapshot | Reads a transaction-consistent version; conflicting writes may fail |

Read committed can use locks or row versions depending on database settings. Stronger isolation may add blocking or version-storage costs. See [SQL Server isolation levels](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql).

The same-key checkout lookup adds `UPDLOCK` and `HOLDLOCK` explicitly. Starting a transaction alone does not provide that lookup protection. See the [checkout lock diagram](../workflows.md#two-requests-with-the-same-idempotency-key).

## Concurrency Control

### Race Condition

A race occurs when overlapping operations produce a result that depends on their timing. Two customers can both read that one unit remains before either writes. A C# stock check alone cannot prevent that race.

### Lost Update

```text
A and B both read balance = 100
A subtracts 10 and writes 90
B subtracts 20 and writes 80
```

The final balance should be 70, but B overwrote A's change. This is a lost update.

### Optimistic Concurrency

Optimistic concurrency checks whether a row changed since it was read. A `rowversion`, version column, or other concurrency token is included in the update condition. If it no longer matches, the caller must reject, reload, or retry appropriately.

The project's `Product.Version` is a concurrency token. Product events also carry that version so search can reject older snapshots. Other entities do not automatically share this protection.

### Pessimistic Locking

Pessimistic locking protects data before changing it. It can be useful for operations likely to conflict, but waiting transactions reduce throughput and may deadlock. Keep the transaction short and lock only what the operation needs.

### Atomic SQL Operation

Put the check and change in one database operation:

```sql
UPDATE Inventory
SET Quantity = Quantity - 1
WHERE Id = 1 AND Quantity > 0;
```

The affected-row count tells you whether the update succeeded. This avoids the gap between reading, checking in C#, and later writing. Checkout uses the same idea for the requested quantity.

### Deadlock

A deadlock occurs when transactions wait for resources held by each other. SQL Server chooses a victim so the other transaction can continue.

Access resources in a consistent order, use short transactions, and reduce lock scope where safe. If retrying a deadlock victim, retry the whole logical transaction with its original idempotency key.

## Connection Pool

Opening a physical SQL connection can require a TCP connection, TLS, authentication, and session setup. A connection pool reuses established connections:

```text
Borrow a connection → execute SQL → return the connection
```

`Close` or `Dispose` usually returns a pooled connection rather than closing its socket. `Max Pool Size` limits the pool, so long transactions can leave other requests waiting. Pooling saves connection setup work; it does not make a slow query faster.

## Database Migration

EF Core migrations describe schema changes:

```bash
dotnet ef migrations add AddPhoneNumber --project src/Ecommerce.Api --output-dir Infrastructure/Persistence/Migrations
dotnet ef database update --project src/Ecommerce.Api
```

These are example commands, not a change required for this checkout. Plan migrations so old and new application versions can coexist during a rollout.

### Expand and Contract Pattern

```text
Add compatible schema → deploy compatible code → backfill data
→ switch reads/writes → remove old code → remove old schema
```

For example, add a new nullable column before requiring every running instance to write it. Remove the old column only after old code no longer uses it.

### Backfill

A backfill updates existing rows to match a new model. Work in manageable batches to control locks, transaction size, and load. Plan how writes arriving during the backfill stay correct.

## Read Replicas

A primary accepts writes and replicates them to read replicas. Read traffic can move to replicas, but replication lag means a recent write may be missing from a replica.

Choose which reads can tolerate stale data. A checkout or immediate confirmation may need the primary. This project uses one SQL Server and has no read-replica routing.

## Partitioning and Sharding

### Partitioning

Partitioning divides a table's data within a database system, for example by order date. Conceptually, 2025 orders and 2026 orders belong to different partitions; this does not require manually creating a table for each year.

#### Partition Pruning

When a query filters on the partition key, the database may skip irrelevant partitions. A query that needs every partition gains less from this layout.

### Sharding

Sharding places subsets of data on independent database nodes. A shard key such as customer ID tells the application where to route a query.

### Hot Shard / Data Skew

A key that concentrates busy customers or data on one shard creates a bottleneck. Plan for uneven traffic and for moving data when the shard layout changes.

### Scatter-Gather Query

A query without a known target shard may query every shard and merge the results. This adds network work and makes sorting and pagination harder.

### Cross-Shard Transaction

A transaction spanning shards needs coordination beyond an ordinary local transaction. Some systems use distributed transactions; others use sagas, eventual consistency, and compensating actions. These choices change the guarantees callers receive. Sharding and sagas are learning topics here.

## Distributed Locking

C# `lock` protects threads in one process. It does not coordinate separate API instances.

Shared systems can coordinate through database locks, constraints, atomic updates, or a distributed lock service. A Redis lock commonly starts with:

```text
SET lock-key unique-token NX PX ttl
```

`NX` acquires only an absent key; `PX` gives it an expiry. Release must check the unique token atomically so one owner cannot delete another's lock. Expiry can occur while the original owner is still working, so sensitive writes may also need fencing tokens or database conditions.

Prefer a database constraint or atomic update when it already solves the problem. This project uses SQL safeguards, not Redis locks.

## Project examples

[ShopDbContext](../../src/Ecommerce.Api/Infrastructure/Persistence/ShopDbContext.cs) defines relationships, unique keys, the nonnegative-stock constraint, and `Product.Version`. Unique keys cover order, payment, and refund replay identities; a processed-message primary key prevents duplicate markers. Shipments and returns also have per-order uniqueness rules.

The customer order list in [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs) filters by owner, uses `AsNoTracking`, sorts by creation time and ID, and projects response fields. The detail query uses `Include` for order items. These are examples of controlling reads, not proof that every query is tuned.

Checkout combines three protections in [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs):

1. A transaction groups stock changes, the order, and the Outbox row.
2. A lookup through the unique key index uses `UPDLOCK` and `HOLDLOCK`. A second request with the same customer/key waits, including when the first order does not exist yet.
3. Conditional stock updates succeed only when enough stock remains.

After the first transaction commits, the waiting same-key request can replay it. A request with different details and the same key is rejected. This combines pessimistic locking for replay identity with atomic inventory updates.

[PaymentService](../../src/Ecommerce.Api/Features/Payments/Services/PaymentService.cs), [RefundService](../../src/Ecommerce.Api/Features/Refunds/Services/RefundService.cs), and fulfillment services also protect operations that can race. Their `FromSqlInterpolated` calls parameterize values while expressing SQL Server lock hints. Ordinary LINQ is suitable when those hints are unnecessary.

### How connection pooling works here

[Program.cs](../../src/Ecommerce.Api/Program.cs) registers a scoped context with `AddDbContext` and `UseSqlServer`. The normal connection string leaves SQL client pooling enabled. EF Core opens a connection when needed and closes it after the operation; an explicit transaction keeps its connection until the transaction ends.

Each HTTP request gets its own scoped `ShopDbContext`; workers create scopes for their work. Three different resources are involved:

| Pool or scope | Reuses or owns |
| --- | --- |
| SQL connection pool | Physical database connections |
| DI scope | Service objects used for one request or worker batch |
| .NET Thread Pool | Threads that execute C# work |

`AddDbContext` does not enable EF Core's separate context-pooling feature. The disposable [verification databases](../../src/Ecommerce.Api/Verification/VerificationDatabase.cs) set `Pooling = false` so they do not retain pooled connections to databases they delete.

### SQL Server and SQLite comparison

| Concern | SQL Server here | SQLite alternative |
| --- | --- | --- |
| Same-key lookup | Uses `UPDLOCK`, `HOLDLOCK`, and a unique-key index | SQL Server hints do not apply |
| Writes | Can lock a row or key range while other work continues | Allows one writer at a time |
| Contention | A waiting same-key request can replay the committed order | May wait or fail with a busy/locked timeout |
| Setup | Needs a server and its resources | Embedded, with no separate database server |
| Rules | Transactions and unique constraints | Also supports transactions and unique constraints |

SQLite can fit production workloads with suitable concurrency and storage requirements. It cannot verify SQL Server-specific locking. The API and current database runners use SQL Server, so their behavior should be checked against that provider. Neither database removes the need for idempotency and constraints.

See [SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table), [transaction locking](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide), [SQLite overview](https://www.sqlite.org/about.html), [SQLite transactions](https://www.sqlite.org/lang_transaction.html), and [Microsoft.Data.Sqlite concurrency](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

---

Previous: [HTTP, ASP.NET Core, and security](http-apis-security.md) · [Learning index](README.md) · Next: [Caching, messaging, and consistency](cache-messaging-consistency.md)
