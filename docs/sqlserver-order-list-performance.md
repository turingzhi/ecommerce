# SQL Server order-list query: measured index change

Measured on 3 October 2026 against the local `EcommerceVerification` SQL Server database. The query matches `GET /orders?page=1&pageSize=20`: filter to one customer, order by newest creation time and ID, and return 20 order summaries.

```sql
SELECT o.Id, o.Status, o.CreatedAt, o.Currency
FROM dbo.Orders AS o
WHERE o.CustomerId = @customerId
ORDER BY o.CreatedAt DESC, o.Id DESC
OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY;
```

The benchmark inserted 20,000 synthetic orders for one customer, warmed the query once, then timed five runs before and after the index. The same rows and query were used for both measurements. The temporary orders were deleted afterward.

| Measure | Before | After |
|---|---:|---:|
| Estimated plan | Clustered index scan + sort | `IX_Orders_CustomerId_CreatedAt_Id` index seek |
| Last execution logical reads (`sys.dm_exec_query_stats`) | 633 | 8 |
| Last server elapsed time | 11.51 ms | 0.14 ms |
| Average client elapsed time, five runs | 12.37 ms | 1.31 ms |

The previous unique index on `(CustomerId, IdempotencyKey)` does not provide rows in the endpoint's requested order. The new index uses `(CustomerId ASC, CreatedAt DESC, Id DESC)` and includes `Status` and `Currency`, so SQL Server can seek to that customer's newest orders and read the projected fields from the index. The index is configured in `Data/ShopDb.cs` and created by the `AddOrderListIndex` migration.

The plan above came from `SET SHOWPLAN_XML ON` (an estimated plan); reads and server time came from the last executed query in `sys.dm_exec_query_stats`. Client time includes the local SQL client and network round trip. These numbers describe one local synthetic workload, not a general latency guarantee. The logical-read and plan changes provide the stronger evidence that the index matches this query.

See the [API reference](api.md) for the order-list endpoint and [verification guide](verification.md) for the current SQL Server checks.
