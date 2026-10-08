# Order-list index measurement

Measured on 3 October 2026 in the local `EcommerceVerification` SQL Server database.
This query matches the first page of the customer's order list:

```sql
SELECT o.Id, o.Status, o.CreatedAt, o.Currency
FROM dbo.Orders AS o
WHERE o.CustomerId = @customerId
ORDER BY o.CreatedAt DESC, o.Id DESC
OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;
```

## Method and results

The test inserted 20,000 synthetic orders for one customer, warmed the query once,
then timed five runs before and after adding the index. Both measurements used the
same data and query. Temporary orders were deleted afterward.

| Measure | Before | After |
| --- | ---: | ---: |
| Estimated plan | Clustered scan + sort | `IX_Orders_CustomerId_CreatedAt_Id` seek |
| Last execution logical reads | 633 | 8 |
| Last server elapsed time | 11.51 ms | 0.14 ms |
| Average client elapsed time, five runs | 12.37 ms | 1.31 ms |

The index orders keys by `(CustomerId ASC, CreatedAt DESC, Id DESC)` and includes
`Status` and `Currency`. SQL can find one customer's newest orders without sorting
all matching rows. The unique idempotency index serves a different lookup.

The estimated plan came from `SET SHOWPLAN_XML ON`. Reads and server time came from
`sys.dm_exec_query_stats`; client time includes the local client and network trip.
These are historical measurements for one workload, not a latency guarantee or a
new benchmark from this documentation update.

The definition is in [ShopDbContext](../src/Ecommerce.Api/Infrastructure/Persistence/ShopDbContext.cs)
and the `AddOrderListIndex` migration. See [verification](verification.md) for current checks.
