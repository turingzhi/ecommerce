# Redis caching and carts

[Documentation index](README.md) · [Project overview](../README.md)

Redis stores temporary product-search responses and authenticated customer carts. SQL Server owns products, inventory, commerce records, and idempotency. The [cart guide](cart.md) covers cart APIs and checkout.

## Keys and request flow

| Key | Value | Expiration |
| --- | --- | --- |
| `products:search:generation` | Numeric catalog cache generation | None set by the application |
| `products:search:v3:{generation}:{hash}` | JSON search response | 30 seconds; hits do not extend it |
| `cart:v1:{customerId}` | Hash of product ID → quantity | Seven days of inactivity; nonempty reads and item writes/removals renew it |

`Program.cs` registers one shared singleton `IConnectionMultiplexer`, which manages
reusable Redis connections and supports concurrent use. `CartService` remains
scoped because it also depends on `ShopDbContext`. A singleton client does not
require every consumer to be singleton. See the
[DI lifetime comparison](knowledge/csharp-fundamentals.md#dbcontext-versus-redis).

A search reads the generation and its result key, queries Elasticsearch on a
miss, then caches the response. An absent generation becomes an empty key
segment; the first `INCR` creates the counter at 1.

The v3 key suffix is SHA-256 of serialized search parameters: trimmed query/category, price bounds, page, pageSize, and sort. Query text is not lowercased. JSON preserves parameter boundaries, preventing delimiter collisions. Older cache namespaces are not read and expire naturally. Colons are ordinary characters in Redis keys.

Sources: [ProductsController](../src/Ecommerce.Api/Features/Catalog/Controllers/ProductsController.cs), [ProductSearchParameters](../src/Ecommerce.Api/Features/Catalog/Contracts/ProductSearchParameters.cs), and [CartService](../src/Ecommerce.Api/Features/Cart/Services/CartService.cs).

## Automatic invalidation

1. Catalog service commits a SQL product change and `ProductUpserted` event.
2. RabbitMQ delivers the event; the consumer writes the versioned Elasticsearch snapshot, waiting for search visibility on a successful write.
3. The consumer increments `products:search:generation`, then saves the SQL processed ID.
4. Later searches use the new generation and fetch fresh results. Older keys expire within 30 seconds.

Changing the generation invalidates every search without scanning keys. It is eventual consistency after event processing. The generation is not a product version or exact update count: retrying after a failed SQL marker can increment it again. Already-processed duplicates are skipped. Backfill with `--index-products` does not increment it. See [EventConsumer](../src/Ecommerce.Api/Infrastructure/Messaging/EventConsumer.cs) and [product synchronization](product-sync.md).

## Start and inspect

Run from the repository root:

```sh
docker compose up -d redis
docker compose exec redis redis-cli PING
docker compose exec redis redis-cli
```

Inside `redis-cli`:

```text
GET products:search:generation
SCAN 0 MATCH products:search:* COUNT 100
INFO memory
INFO keyspace
DBSIZE
```

Repeat `SCAN` with its returned cursor until it is 0. `COUNT` is a hint. Copy an actual result key and run `GET`, `TYPE`, or `TTL` on it. TTL is seconds remaining: `-1` means no expiration, `-2` means absent. Search entries may expire during inspection. `QUIT` exits.

## Check caching and invalidation

```sh
curl -s 'http://127.0.0.1:5088/products/search?q=wireless'
curl -s 'http://127.0.0.1:5088/products/search?q=wireless'
docker compose logs --since=1m ecommerce | rg 'Search cache|Redis'
```

With no existing result, expect MISS then HIT within 30 seconds, and another MISS after expiration. Cached JSON may show escaped quotes in `redis-cli`.

For invalidation, record the current generation, change a sample product, and inspect the generation again after delivery:

```sh
docker compose exec -T redis redis-cli GET products:search:generation
docker compose exec -T ecommerce dotnet Ecommerce.Api.dll   --update-product 2 "Wireless Mouse" "Updated Redis example" "Accessories" 2000
docker compose exec -T redis redis-cli GET products:search:generation
```

Replace ID `2` if your catalog differs. Allow time for Outbox polling and consumption. Search again promptly: the first result under the new generation should be a MISS with the changed description, followed by a HIT with that description. A generation change alone does not prove freshness, and expiration can also cause a MISS. These are manual learning checks.

## Failure behavior and limits

Search catches `RedisException` on cache reads/writes and falls back to Elasticsearch; Redis timeouts can delay it. A cache hit can succeed while Elasticsearch is down. A miss requires Elasticsearch and returns 503 if search is unavailable.

Cart operations require Redis and return 503 on Redis failure. Replaying a completed cart checkout checks SQL first and does not require Redis. With Redis registered, a failed product-event generation increment prevents the processed marker and follows RabbitMQ's counted retry policy. Verification consumers that omit Redis skip invalidation.

After five counted Redis failures, a valid product event can be in the dead queue;
restoring Redis does not replay it automatically. Elasticsearch may already hold
the update, and old cache entries still expire after 30 seconds. The
[review](architecture-review.md#7-redis-outages-exhaust-valid-product-event-retries--medium-limitation)
proposes separate retry handling for Redis connection/timeouts. That change is pending.

To exercise search fallback locally:

```sh
docker compose stop redis
curl -i 'http://127.0.0.1:5088/products/search?q=wireless'
docker compose logs --since=1m ecommerce | rg 'Redis|Search cache'
docker compose start redis
```

Redis has a PING health check. `/health` checks SQL only; `/health/dependencies` includes Redis. Compose gives Redis no persistence volume, memory cap, or eviction policy. Recreating its container can lose carts and cache state. There is no search stampede protection or atomic transaction across SQL, Elasticsearch, and Redis.

## CLI practice

Use separate learning keys:

```text
SET learning:name Alice EX 120
GET learning:name
TTL learning:name
SET learning:name Bob KEEPTTL
SET learning:once first NX EX 120
SET learning:once second NX EX 120
SET learning:once third XX KEEPTTL
SET learning:counter 10
INCR learning:counter
HSET learning:cart:alice product:1 2 product:2 1
HINCRBY learning:cart:alice product:1 1
HGETALL learning:cart:alice
EXPIRE learning:cart:alice 300
HDEL learning:cart:alice product:2
DEL learning:name learning:once learning:counter learning:cart:alice
```

Keys have no default expiration. `SET` removes an existing TTL unless `KEEPTTL` is used. `NX` requires an absent key; `XX` requires an existing key. Hash fields hold quantities in this exercise. Use learning keys instead of editing live cart or generation keys.
