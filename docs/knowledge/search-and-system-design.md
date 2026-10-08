# Search and system design

[Learning index](README.md) · [Documentation index](../README.md)

Learn how search differs from transaction storage, then practice choosing system components based on the problem they solve.

**In this project:** SQL Server owns product price and stock. Elasticsearch holds a versioned search copy, updated through the Outbox and RabbitMQ. Redis caches search results. The [search service](../../src/Ecommerce.Api/Infrastructure/Search/ProductSearchService.cs) supports text search, category and price filters, sorting, and pagination. The React and TypeScript [storefront](../storefront.md) uses these APIs.

## On this page

- [Search Engines](#search-engines)
- [SQL Database vs Search Engine](#sql-database-vs-search-engine)
- [Common System Design Trade-offs](#common-system-design-trade-offs)
- [Complete Request Flow](#complete-request-flow)
- [Example Modern C# Backend Architecture](#example-modern-c-backend-architecture)
- [Core Engineering Principles](#core-engineering-principles)
- [Recommended Learning Order](#recommended-learning-order)
- [Core Interview Questions You Should Be Able to Explain](#core-interview-questions-you-should-be-able-to-explain)
- [Final Goal](#final-goal)
- [Exercises in this repository](#exercises-in-this-repository)

## Search Engines

Elasticsearch and OpenSearch store searchable documents and provide tools for matching and ranking them.

### Why not just use SQL LIKE?

`LIKE` can suit a simple text filter. Search engines add text analysis, relevance ranking, fuzzy matches, autocomplete, synonyms, and facets. They also add another service and a synchronization problem. Choose them when those features justify the added work.

This project matches product names and descriptions and filters category and price. Fuzzy search, autocomplete, synonyms, and faceted counts are topics to study; they are not all enabled here.

### Document and Index

A document is one searchable record, such as a product with its name, description, category, price, and version. An index groups documents and defines how their fields are stored and searched.

The project's [ProductSearchDocument](../../src/Ecommerce.Api/Infrastructure/Search/ProductSearchDocument.cs) is a copy of selected catalog fields, not the SQL entity itself.

### Inverted Index

An inverted index maps terms to the documents containing them:

```text
"wireless" → product 1, product 2
"mouse"    → product 2
```

This makes term lookup efficient without checking every document's text.

### Text Analysis

A tokenizer splits text into tokens. An analyzer combines tokenization with transformations such as lowercasing, removing stop words, or stemming words to a common form. Lemmatization uses linguistic rules to find a base word.

Analysis at indexing and query time determines which terms match. For example, a lowercase analyzer can make `Wireless` and `wireless` match. Do not assume that every language-specific transformation is configured by default.

### Relevance Ranking

Ranking gives stronger matches higher scores. TF-IDF and BM25 use ideas such as term frequency and how common a term is across documents. Field selection and scoring affect whether the first result is useful to a user.

The project sorts by score for relevance or by price, with product ID as a tie-breaker. Measure result quality as well as speed.

### Faceted Search

Facets group matching documents into counts, such as category, brand, or price range. Search engines often compute them with aggregations. A category filter narrows results; a category facet also reports how many matches each category has. The current API provides filters, not a complete faceting feature.

## SQL Database vs Search Engine

Use the relational database for authoritative data and transactions. Use the search index as a query-friendly copy:

```text
SQL commit → Outbox → RabbitMQ → consumer → Elasticsearch
```

A committed catalog update can reach search later. Checkout reads price and stock from SQL so a stale search result cannot set the amount charged or oversell stock. See [product synchronization](../product-sync.md).

### Denormalization

A search document often duplicates fields that live in several relational tables. This makes reads simpler but means every copy needs an update strategy.

### Reindexing

A changed mapping or analyzer may need a new index. A common rollout creates `products-v2`, backfills it, catches up new changes, validates it, and switches an alias from the old index. Keep a rollback plan and avoid missing writes during the rebuild.

This project's indexing command writes to its configured products index; it does not implement that alias-based rollout.

## Common System Design Trade-offs

| Choice | Question to answer |
| --- | --- |
| Consistency and availability | During a failure, should a read wait, fail, or return older data? |
| Latency and durability | What must be saved or confirmed before responding? |
| Read and write performance | Which operations matter most, and what extra write work is acceptable? |
| Normalization and denormalization | How many copies of a value must stay current? |
| Simplicity and scalability | Does the workload justify another component? |
| Cost and redundancy | Which failures must the system survive? |
| Guarantees and throughput | What coordination is needed to enforce the rule? |

Describe a choice with an example: “Search may lag, but checkout uses current SQL price and stock.” That is more useful than listing technologies.

## Complete Request Flow

Before sending an HTTP request, the client may resolve a hostname through DNS and establish a connection with TLS. DNS finds the destination; it does not forward the request.

A larger deployment might then route a request through:

```text
Client → CDN/WAF → load balancer → reverse proxy or gateway
       → ASP.NET Core middleware → endpoint → service
       → database, cache, or external API → response
```

These roles can overlap, and some components may be absent. A service may save an Outbox row for later event delivery rather than publish during the request. This repository's local Compose setup does not have the edge or gateway layer; see its [architecture](../architecture.md).

## Example Modern C# Backend Architecture

This general example shows how components can fit together:

```text
Browser → edge/proxy → ASP.NET Core
                        ├→ Redis: shared cache or temporary state
                        ├→ SQL database: business data + Outbox
                        │                   ↓
                        │                RabbitMQ → workers → search or jobs
                        └→ object storage: file metadata and upload permissions

Large file: browser → signed URL → object storage → CDN
```

The current app uses SQL Server, Redis, RabbitMQ, and Elasticsearch. It serves the built storefront itself and has shipment and return workflows. Object storage, a CDN, an API gateway, and email delivery are learning examples, not deployed components here.

## Core Engineering Principles

1. Start with the simplest solution that meets the requirement.
2. Make workflow state and allowed transitions explicit.
3. Use transactions for changes in one database.
4. Use stable idempotency keys when retries are possible.
5. Expect separate services to fail independently.
6. Await I/O instead of blocking request threads.
7. Decide how caches expire and become current.
8. Prefer database constraints or atomic updates for stored rules.
9. Use logs, metrics, and traces to explain behavior.
10. Plan timeouts, retries, and partial outages.
11. Check both identity and permission.
12. Validate untrusted input.
13. Keep durable state outside individual API instances.
14. Name the authoritative store for each kind of data.
15. Roll out changes that work with the previous version.
16. Measure the bottleneck before adding capacity.

## Recommended Learning Order

Follow the [seven-chapter reading order](README.md#reading-order). Trace an implemented request and event first. Then study an additional component when you can explain what problem it would solve. Use the [exercises](#exercises-in-this-repository) to connect the concepts to code.

## Core Interview Questions You Should Be Able to Explain

- How does `async/await` help a web server handle more requests?
- How do the Thread Pool and connection pool differ?
- What does a transaction guarantee, and what does it leave to application rules?
- When would you use optimistic or pessimistic concurrency?
- Why can an index speed up reads but slow down writes?
- What is an N+1 query pattern?
- How does cached data become current after a database change?
- How would you prevent a cache stampede?
- How do RabbitMQ queues and Kafka logs differ?
- What failure window does the Outbox close?
- What makes an operation idempotent?
- How do you prevent duplicate effects with at-least-once delivery?
- What does eventual consistency allow a client to observe?
- What does a saga coordinate, and how can compensation fail?
- How do JWT, OAuth 2.0, and OpenID Connect differ?
- When should an API return `401` or `403`?
- What do a proxy, load balancer, and gateway each do?
- What problems does replica lag cause?
- How do shard keys affect queries, hotspots, and transactions?
- What does CAP say during a network partition?
- How can retries make an outage worse?
- What does a circuit breaker do?
- How do liveness and readiness differ?
- How do CORS and CSRF differ?
- How do parameterized queries prevent SQL injection?
- What is SSRF?
- Why might files belong in object storage instead of an API instance's disk?
- Why does a search index not replace the transaction database here?
- How can old and new code share a changing database schema?

## Final Goal

For any proposed design, explain where data lives, which store is authoritative, and what a user may see after a write. Then explain failure and retry behavior, concurrent writes, scaling, observation, deployment, and access control.

Use a concrete failure to test the design. For example: “SQL saved the order, but the response was lost. The client retries the same key. Which result does it receive, and is stock reserved again?”

## Exercises in this repository

- Trace a checkout through [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs), its transaction, and the Outbox. Compare the result with [HTTP checks](../../tests/http/verify_checkout_http.py).
- Try the existing search filters, sorts, and page bounds. Inspect the cache key and Elasticsearch request, then measure them with a larger catalog. Consider facets or autocomplete as a separate enhancement.
- Follow an `OrderPaid` event into shipment creation, history, and [returns](../fulfillment.md). Identify which changes share a transaction and which happen later.
- Use the existing [telemetry viewer](../observability.md) and [health checks](../health.md) to inspect a dependency outage. Explain what a healthy response does and does not establish.
- Plan coordination of Outbox workers and migrations before running multiple API replicas. The current local setup uses one API instance.
- Design provider callbacks and reconciliation before adding a real payment integration. Current payment/refund outcomes use a [Development simulator](../payments.md).

---

Previous: [Caching, messaging, and consistency](cache-messaging-consistency.md) · [Learning index](README.md) · Next: [Reliability, observability, and testing](reliability-observability-testing.md)
