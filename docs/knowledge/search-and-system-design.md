# Search and system design

[Learning index](README.md) · [Documentation index](../README.md)

Connect search indexing to the wider system. Use the trade-offs, example flows, and questions to practice explaining design decisions. Code snippets are general examples unless they link to a repository file.

> **In this project:** SQL Server is authoritative for catalog stock and price. Elasticsearch is a versioned search projection updated through Outbox and RabbitMQ, so search can lag after a write. The generic architectures in this chapter are examples, not diagrams of this repository. See [product synchronization](../product-sync.md), [search service](../../Search/ProductSearchService.cs), and the [project architecture](../architecture.md#follow-one-request-through-the-system).

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

Common systems:

- Elasticsearch
- OpenSearch

### Why not just use SQL LIKE?

Search engines support:

- Full-text search
- Relevance ranking
- Fuzzy search
- Autocomplete
- Synonyms
- Faceted search

### Document and Index

Search engines are built around documents.

### Inverted Index

```text
Term
→ Documents containing that term
```

### Text Analysis

Common concepts:

- Tokenizer
- Analyzer
- Lowercasing
- Stop words
- Stemming
- Lemmatization

### Relevance Ranking

Common models:

- TF-IDF
- BM25

### Faceted Search

Often built with:

**Aggregations**

for filtering by:

- Brand
- Category
- Price range
- Attributes

## SQL Database vs Search Engine

A common architecture:

```text
SQL Database
→ Source of Truth

Elasticsearch
→ Search Projection
```

Synchronization:

```text
Database
↓
Outbox
↓
Message Broker
↓
Search Indexer
↓
Elasticsearch
```

This is usually:

**Eventually Consistent**

### Denormalization

Search documents often duplicate fields to improve query performance.

### Reindexing

A common strategy:

```text
products-v1
↓
products-v2
↓
switch alias
```

## Common System Design Trade-offs

There is no perfect architecture.

Typical trade-offs:

```text
Consistency vs Availability
Latency vs Durability
Read Performance vs Write Performance
Normalization vs Denormalization
Simplicity vs Scalability
Cost vs Redundancy
Strong Guarantees vs Throughput
```

The important skill is not memorizing technologies.

It is understanding:

> Why is this technology used, and what trade-off does it introduce?

## Complete Request Flow

A request such as:

```text
https://api.example.com/orders/123
```

may involve the following components. DNS resolves the destination before the HTTP connection; it is not a proxy that forwards the request. Edge, gateway, and proxy roles can overlap, so a real deployment need not contain every box:

```text
Client
↓
DNS
↓
CDN / Edge
↓
TLS
↓
WAF
↓
Load Balancer
↓
API Gateway / Reverse Proxy
↓
ASP.NET Core
↓
Middleware / endpoint routing
↓
Authentication
↓
Authorization
↓
Endpoint handler
↓
Service
├→ Redis
├→ Database
├→ External API
└→ Message Broker
↓
Response
```

## Example Modern C# Backend Architecture

```text
                    Client
                       ↓
                      DNS
                       ↓
                 CDN / WAF
                       ↓
                Load Balancer
                       ↓
                 API Gateway
                       ↓
        ┌──────── ASP.NET Core ────────┐
        │                              │
        ↓                              ↓
      Redis                        PostgreSQL
        │                              │
        │                           Outbox
        │                              ↓
        │                         RabbitMQ
        │                              ↓
        │                           Workers
        │                           /      \
        │                          /        \
        ↓                         ↓          ↓
    Cached Data             Email/Jobs   Elasticsearch

Files:
Client → Signed URL → Object Storage → CDN
```

## Core Engineering Principles

1. Prefer simple solutions first.
2. Make state explicit.
3. Use transactions for local atomicity.
4. Use idempotency when retries are possible.
5. Assume distributed operations can fail independently.
6. Avoid blocking I/O.
7. Use caches carefully; invalidation matters.
8. Prefer atomic database operations over application-level locks where possible.
9. Observe the system with logs, metrics, and traces.
10. Design for failure, timeout, retry, and partial outage.
11. Authentication and Authorization are different.
12. Never trust user input.
13. Prefer stateless application instances.
14. Keep a clear Source of Truth.
15. Deploy backward-compatible changes.
16. Scale after measuring the real bottleneck.

## Recommended Learning Order

Follow the [seven-chapter reading order](README.md#reading-order). First trace an implemented request and event, then explore the additional infrastructure when you can explain the problem it solves. The [project exercises](#exercises-in-this-repository) provide concrete next steps.

## Core Interview Questions You Should Be Able to Explain

You should be able to clearly answer questions such as:

- Why does `async/await` improve web server scalability?
- What is the difference between a Thread Pool and a Connection Pool?
- What does a database Transaction guarantee?
- What is the difference between Optimistic and Pessimistic Concurrency?
- Why do indexes speed up reads, and why not add indexes everywhere?
- What is the N+1 Problem?
- How do Redis and the database stay consistent?
- How do you prevent Cache Stampede?
- What is the difference between RabbitMQ and Kafka?
- Why is the Outbox Pattern needed?
- What is Idempotency?
- Why can at-least-once delivery produce duplicate deliveries, and how do you prevent duplicate effects?
- What is Eventual Consistency?
- What problem does Saga solve?
- What is the difference between JWT, OAuth 2.0, and OpenID Connect?
- What is the difference between HTTP 401 and 403?
- What is the difference between a Load Balancer, Reverse Proxy, and API Gateway?
- What problems do Read Replicas introduce?
- What is the hardest part of Sharding?
- What does the CAP Theorem actually mean?
- Why can Retry make an outage worse?
- What is a Circuit Breaker?
- What is the difference between Liveness and Readiness?
- What is the difference between CORS and CSRF?
- How do you prevent SQL Injection?
- What is SSRF?
- Why should large files not usually be stored on the API server's local disk?
- Why does Elasticsearch not simply replace a relational database?
- How do you perform a zero-downtime database migration?

## Final Goal

Use these questions to review a proposed design:

```text
Where should the data live?
What is the Source of Truth?
What consistency guarantee is required?
What happens if one step fails?
Will the system retry?
Can retry cause duplicate effects?
Can concurrent requests conflict?
How will the system scale?
How will the system be observed?
How will it be deployed safely?
How will it be secured?
```


## Exercises in this repository

- Expand the existing [HTTP smoke/E2E script](../../scripts/verify_http.py) with additional endpoint and response-contract cases. It already covers registration, login, ownership, checkout replay, and payment-attempt replay. Payment outcomes and refunds need public endpoints before they can be tested through HTTP.
- Add catalog search filters, sorting, and pagination, then measure their query behavior.
- Coordinate background workers before running multiple API replicas.
- Add metrics/traces and a health check that distinguishes SQL, RabbitMQ, and Elasticsearch readiness; then plan deployment beyond the local Compose stack.
- Integrate a payment provider only after defining callbacks, reconciliation, and operational failure handling. Current outcomes are local simulations.

---

Previous: [Caching, messaging, and consistency](cache-messaging-consistency.md) · [Learning index](README.md) · Next: [Reliability, observability, and testing](reliability-observability-testing.md)
