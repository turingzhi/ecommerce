# Local workflow demonstration

Run these commands from the `Ecommerce` directory with Docker Desktop running. On a first run, copy `.env.example` to `.env` and set `MSSQL_SA_PASSWORD` to a strong local password. The commands below write sample data to the Compose SQL Server volume; `docker compose down` keeps it, while `docker compose down -v` deletes it.

## Successful flow: product to order to payment attempt

1. Start the four services and check the API:

   ```sh
   docker compose up -d --build --wait
   curl -i http://127.0.0.1:5088/health
   ```

   Expect HTTP 200 and `{"status":"healthy"}`. SQL Server migrations and sample products are applied at startup.

2. Create a product through the catalog service. Copy the product ID printed by the command:

   ```sh
   demo_name="Demo Camera $(date +%s)"
   docker compose exec -T ecommerce dotnet Ecommerce.dll --create-product "$demo_name" "Wide angle demo camera" "Photo" 12000 3
   curl -G --data-urlencode "q=$demo_name" http://127.0.0.1:5088/products/search
   ```

   The SQL product and its `ProductUpserted` Outbox event commit together. Repeat the search after a few seconds if it initially returns `[]`: the worker, RabbitMQ, consumer, and Elasticsearch run asynchronously. The result should show the same product ID and price `12000` cents.

3. In [auth.http](../Http/auth.http), register a fresh local account, then log in and copy the returned `accessToken`. In [orders.http](../Http/orders.http), replace `PASTE_ACCESS_TOKEN_HERE` and change the example body to **one item** with the new product ID and quantity `1`. Send `POST /orders` twice with the **same** `Idempotency-Key` and body. Expect HTTP 201, then HTTP 200 with the **same order ID**. Only one unit is reserved.

4. Copy that order ID into [payments.http](../Http/payments.http), along with the access token. Send `POST /orders/{orderId}/payments` twice with the same key. Expect HTTP 201, then HTTP 200 with the **same payment ID** and amount `12000` cents. The payment remains `Pending`: creating an attempt does not charge money. Payment success/failure and refunds are service-level simulations covered by the verification runners, not public HTTP endpoints.

The order and payment IDs stay the same on replay because each idempotency key identifies an existing request. SQL Server determines stock and price; Elasticsearch is a searchable copy, not the checkout database. The Outbox separates the committed SQL transaction from later RabbitMQ publication.

## Outage and recovery: Elasticsearch goes offline

Keep the API, SQL Server, and RabbitMQ running. Stop only search:

```sh
docker compose stop elasticsearch
curl -i http://127.0.0.1:5088/health
curl -i 'http://127.0.0.1:5088/products/search?q=wireless'
```

Health should still return 200 because it checks SQL Server. Search should return 503. While Elasticsearch is down, create another product:

```sh
recovery_name="Recovery Camera $(date +%s)"
docker compose exec -T ecommerce dotnet Ecommerce.dll --create-product "$recovery_name" "Created during search outage" "Photo" 13000 2
docker compose up -d --wait elasticsearch
curl -G --data-urlencode "q=$recovery_name" http://127.0.0.1:5088/products/search
```

Repeat the last search until the product appears. The CLI confirms that SQL Server saved the product even during the search outage. The API's Outbox worker publishes the event; the consumer retains failed product deliveries in RabbitMQ's durable retry queue and indexes the product after Elasticsearch recovers. There can be a short delay for message retry and search-index refresh. Do not use `--index-products` here: this example demonstrates **automatic** recovery.

The SQL write succeeds independently of search. `/health` checks SQL Server but not Elasticsearch, so search can return 503 while health remains 200. SQL Server remains authoritative throughout the outage.

See the [API reference](api.md) for endpoint details and [product synchronization](product-sync.md) for the recovery path.
