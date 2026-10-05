# Data access and concurrency

[Learning index](README.md) · [Documentation index](../README.md)

Follow data from EF Core queries to SQL indexes, transactions, concurrent writes, and schema changes. Replicas, sharding, and distributed locks extend the discussion to larger systems. Code snippets are general examples unless they link to a repository file.

> **In this project:** The app uses EF Core with SQL Server, explicit transactions, unique indexes, SQL Server lock hints, and conditional stock updates. It does not use read replicas, sharding, or Redis locks. Disposable SQL Server verification databases explicitly disable connection pooling. See [ShopDb](../../Data/ShopDb.cs), [OrderService](../../Services/OrderService.cs), and the [SQL lock diagram](../workflows.md#two-requests-with-the-same-idempotency-key).

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

`DbContext` is responsible for:

- Change Tracking
- Querying
- `SaveChanges`
- Transaction integration

Example:

```csharp
var user = await db.Users
    .FirstOrDefaultAsync(x => x.Id == id);

await db.SaveChangesAsync();
```

### IQueryable

`IQueryable` represents a query that has not necessarily executed yet.

```csharp
var query = db.Users
    .Where(x => x.IsActive);

var result = await query.ToListAsync();
```

Execution usually happens at:

```text
ToListAsync
FirstAsync
CountAsync
SingleAsync
```

### Projection

Avoid loading more data than necessary.

Instead of:

```csharp
await db.Users.ToListAsync();
```

Use projection:

```csharp
await db.Users
    .Select(x => new UserDto
    {
        Id = x.Id,
        Name = x.Name
    })
    .ToListAsync();
```

Benefits:

- Less network traffic
- Less memory usage
- Less database I/O

### AsNoTracking

For read-only queries:

```csharp
db.Users.AsNoTracking()
```

This reduces Change Tracking overhead.

### N+1 Problem

Example:

```text
Load 100 Orders
↓
Load one User for each Order
↓
1 + 100 queries
```

Common solutions:

- `Include`
- `JOIN`
- Projection
- Batch queries

### Tracked changes versus immediate updates

Changing a tracked entity property is saved by `SaveChangesAsync`. `ExecuteUpdateAsync` sends an update immediately and does not refresh already tracked objects or automatically apply their concurrency tokens. Several such updates and a later save need an explicit transaction when they must commit together. Check the affected-row count to detect a failed condition. See [EF Core bulk updates](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete).

In [OrderService](../../Services/OrderService.cs), stock is reduced with a conditional `ExecuteUpdateAsync`; zero affected rows means checkout cannot continue. The surrounding transaction keeps those immediate stock updates atomic with the later order and Outbox save.

## Database Indexes

Indexes can improve queries whose filters, joins, or ordering match the index. Measure the execution plan and reads; an unused index still adds write and storage cost.

Example:

```sql
CREATE INDEX IX_Users_Email
ON Users(Email);
```

Useful for:

- `WHERE`
- `JOIN`
- `ORDER BY`
- Frequently queried columns

Trade-offs:

- Slower writes
- Extra disk usage
- Maintenance cost

### Composite Index

Example:

```text
INDEX(UserId, CreatedAt)
```

Column order matters.

### Covering Index

If an index contains all columns required by a query, the database may avoid looking up the base table.

## Transactions and ACID

### ACID

- **Atomicity** — all operations succeed or all fail
- **Consistency** — database constraints remain valid
- **Isolation** — concurrent transactions are isolated from each other
- **Durability** — committed data survives failures

### Transaction

```csharp
await using var tx =
    await db.Database.BeginTransactionAsync();

try
{
    // operations

    await db.SaveChangesAsync();
    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();
    throw;
}
```

A single `SaveChangesAsync()` normally executes its own database changes transactionally.

If multiple saves must succeed or fail together, use an explicit transaction.

### Isolation levels

A transaction groups changes; its isolation level determines what concurrent transactions can observe. SQL Server offers these choices:

| Level | Main read guarantee or trade-off |
| --- | --- |
| Read uncommitted | May read changes that later roll back |
| Read committed | Avoids dirty reads; repeated statements can see different committed data |
| Repeatable read | Protects rows read until transaction end; matching new rows can still appear |
| Serializable | Also protects qualifying key ranges against new matching rows |
| Snapshot | Reads a transaction-consistent version; conflicting writes may fail |

Read committed can use locks or row versions depending on database configuration. Stronger isolation can add blocking or versioning costs. See [SQL Server isolation levels](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql).

This project's same-key lookup uses explicit `UPDLOCK` and `HOLDLOCK` hints. Merely adding `BeginTransactionAsync` would not provide that same lookup protection. See the [concurrent checkout diagram](../workflows.md#two-requests-with-the-same-idempotency-key).

## Concurrency Control

### Race Condition

A race condition happens when multiple concurrent operations compete over shared state.

### Lost Update

Example:

```text
A reads balance = 100
B reads balance = 100

A writes 90
B writes 80
```

A's update is lost.

### Optimistic Concurrency

Assumes conflicts are uncommon.

Common techniques:

- `rowversion`
- Version column
- Concurrency token

The update checks whether the version is still unchanged.

### Pessimistic Locking

Locks data before changing it.

Good for high-contention scenarios, but may cause:

- Blocking
- Deadlocks
- Reduced throughput

### Atomic SQL Operation

Prefer atomic database operations when possible:

```sql
UPDATE Inventory
SET Quantity = Quantity - 1
WHERE Id = 1
AND Quantity > 0;
```

This is safer than:

```text
SELECT
↓
application logic
↓
UPDATE
```

### Deadlock

Two transactions wait for resources held by each other.

Common mitigation:

- Consistent resource access order
- Short transactions
- Retry logic
- Smaller lock scope

## Connection Pool

Opening a database connection may involve:

```text
TCP handshake
TLS handshake
Authentication
Session creation
```

A Connection Pool reuses established connections:

```text
Rent Connection
↓
Execute SQL
↓
Return Connection
```

Important points:

- The pool reuses physical connections
- `Close` / `Dispose` often returns the connection to the pool
- It does not necessarily close the TCP connection
- `Max Pool Size` limits the number of pooled connections

Connection Pooling reduces:

- Connection establishment overhead
- Repeated authentication/setup cost

It does not make SQL execution itself faster.

## Database Migration

EF Core examples:

```bash
dotnet ef migrations add AddPhoneNumber
dotnet ef database update
```

The most important production concern is:

**Backward Compatibility**

### Expand and Contract Pattern

```text
Expand schema
↓
Deploy compatible code
↓
Backfill data
↓
Switch reads/writes
↓
Remove old code
↓
Contract schema
```

### Backfill

Update existing historical data gradually instead of locking a huge table with one massive operation.

## Read Replicas

Architecture:

```text
Primary
↓ Replication
Replica 1
Replica 2
```

Writes:

```text
Primary
```

Reads:

```text
Replicas
```

Main problem:

**Replication Lag**

This can lead to:

**Stale Reads**

## Partitioning and Sharding

### Partitioning

Split a large table into logical partitions inside a database system.

Example:

```text
Orders_2025
Orders_2026
```

#### Partition Pruning

The database accesses only relevant partitions.

### Sharding

Distribute data across independent database nodes.

```text
UserId
↓
Shard
```

The key choice is:

**Shard Key**

### Hot Shard / Data Skew

A poor shard key may cause one shard to receive too much data or traffic.

### Scatter-Gather Query

If the target shard is unknown:

```text
Query all shards
↓
Merge results
```

This is expensive.

### Cross-Shard Transaction

Transactions across shards are difficult.

Common approaches:

- Saga
- Eventual Consistency
- Compensation

## Distributed Locking

A local lock:

```csharp
lock (...)
```

only protects one process.

Distributed systems may use:

- Database locking
- Redis-based locking
- Atomic database operations

Redis-style lock concept:

```text
SET key value NX PX ttl
```

Use a unique token to prevent one process from releasing another process's lock.

General rule:

> Prefer atomic database operations or constraints when possible instead of distributed locks.

## Project examples

[ShopDb](../../Data/ShopDb.cs) maps entities and constraints. `IQueryable` operations such as `Where` and `Select` build a SQL query; `ToListAsync` or `SingleOrDefaultAsync` executes it. The paginated `GET /orders` query in [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) filters by customer, uses `AsNoTracking`, orders by creation time, and projects only response fields. The order-detail query uses `Include` to load its items. These are concrete ways to control what data EF Core reads; they do not mean every query in the app has been performance-tuned.

The model has a unique `(CustomerId, IdempotencyKey)` index, similar unique keys for payment and refund attempts, a customer/date order-list index, a stock check constraint, and a processed-message primary key. Indexes make particular reads and uniqueness checks efficient, but cost storage and write work. See the [local order-list index measurement](../sqlserver-order-list-performance.md) for one measured example.

Checkout combines three safeguards in [OrderService](../../Services/OrderService.cs):

1. A transaction groups stock updates, the order, and the Outbox row. An early return or failure rolls back uncommitted changes.
2. A SQL Server `UPDLOCK`/`HOLDLOCK` lookup through the unique key index makes another request with the same key wait, even when no order exists yet. The second request can then return the first order as a replay.
3. A conditional `ExecuteUpdateAsync` reduces stock only when enough remains. The database checks the condition as part of the update, so two buyers cannot both reserve the last unit.

This combines **pessimistic locking** for same-key checkout with an **atomic update** for inventory. It is more precise than “read, check in C#, then write.” [PaymentService](../../Services/PaymentService.cs) and [RefundService](../../Services/RefundService.cs) also use SQL Server locking for operations that can race. The `Product.Version` model property is a concurrency token, and product search additionally uses that version to reject stale indexing events; this does not mean every entity uses optimistic concurrency. See the [SQL lock sequence](../workflows.md#two-requests-with-the-same-idempotency-key).

`FromSqlInterpolated` in these services passes values as parameters while expressing SQL Server lock hints. A normal LINQ lookup, such as `SingleOrDefaultAsync`, is appropriate when the query does not need those hints. The explicit SQL is about the required lock behavior, not about SQL Server generally taking longer to answer a query.

### How connection pooling works here

The normal API registers a scoped `ShopDb` and chooses the SQL Server provider in [Program.cs](../../Program.cs):

```csharp
builder.Services.AddDbContext<ShopDb>(options =>
    options.UseSqlServer(connectionString));
```

There is no `Pooling=false` in the application's connection string, so the SQL client uses its **default connection pool**. When EF Core needs SQL Server, it opens a connection that the client can take from this pool. After EF Core finishes and closes the connection, the client can return it for another operation. An explicit transaction keeps its connection in use until that transaction ends. This pool belongs to the application's SQL client; it is not a table or setting that this project creates inside SQL Server.

Each HTTP request gets its own scoped `ShopDb`. Background workers create their own scopes. **A `ShopDb` instance is not a pooled SQL connection:** `AddDbContext` does not enable EF Core's separate `DbContext` pooling feature. The [.NET thread pool](csharp-fundamentals.md#thread-pool-and-asyncawait) is also separate; it supplies threads to run C# code.

The disposable [verification databases](../../Verification/VerificationDatabase.cs) explicitly set `Pooling = false` in their SQL connection strings. Those checks create and delete temporary databases, so they avoid keeping connections to a database they are about to delete. The normal API leaves pooling enabled.


### SQL Server and SQLite comparison

| | SQL Server used by this project | SQLite alternative |
| --- | --- | --- |
| Key lookup | Uses `FromSqlInterpolated` with `UPDLOCK`, `HOLDLOCK`, and the unique-key index | Uses an ordinary EF Core `Where` query; SQL Server hints do not apply |
| Concurrent writes | Locks the matching key or missing-key range until the transaction ends, so another same-key checkout waits | SQLite allows only one writer at a time, rather than locking an individual order key |
| Result under contention | After the first transaction commits, the waiting same-key call can read and replay its order | A competing operation may wait, retry, or fail with a busy/locked timeout; do not assume the SQL Server wait-and-replay behavior |
| Shared safeguards | The transaction keeps stock, order, and Outbox atomic; the unique `(CustomerId, IdempotencyKey)` index prevents a second order for that key | Transactions and unique indexes are also available in SQLite, but are not used by this project's current runners |

Practical pros and cons for this project:

| Database | Advantages here | Trade-offs here |
| --- | --- | --- |
| SQL Server | Supports many concurrent clients with targeted locking; the API and SQL Server verification exercise the same database behavior | Requires a running server and more setup/resources; the SQL Server-specific locking query must be maintained and checked |
| SQLite | Embedded and serverless; still supports transactions and unique indexes | Only one writer at a time; contention can cause waits or busy timeouts; it cannot verify SQL Server's `UPDLOCK`/`HOLDLOCK` behavior |

The API and all current verification runners use SQL Server. The `--verify` runner covers business rules in disposable databases, while `--verify-sqlserver` adds focused lock and concurrency checks. Neither database removes the need for idempotency keys and database constraints. SQLite can also serve production applications when its concurrency and deployment model fit their needs. For the database rules behind this comparison, see [SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table), [SQL Server transaction locking](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide), [SQLite overview](https://www.sqlite.org/about.html), [SQLite transactions](https://www.sqlite.org/lang_transaction.html), and [Microsoft.Data.Sqlite concurrency](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions).

---

Previous: [HTTP, ASP.NET Core, and security](http-apis-security.md) · [Learning index](README.md) · Next: [Caching, messaging, and consistency](cache-messaging-consistency.md)
