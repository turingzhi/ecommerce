# Project workflow diagrams

These diagrams show the workflows implemented in this project. SQL Server is the source of truth. Payments and refunds have simulated outcomes; there is no bank integration or transfer of real money. The HTTP API exposes registration, login, product search, order creation and reading, and payment-attempt creation. Expiration runs automatically in a background worker. Cancellation, payment outcomes, and refunds are service-level operations rather than public HTTP endpoints. See the [API reference](api.md) for request and response behavior.

## Customer, order, and stock

```mermaid
flowchart TD
    A[Search products; login not required] --> B[Register or log in before checkout]
    B --> C[POST /orders with Idempotency-Key]
    C --> D{Was this key used before?}
    D -->|Same items| E[Return existing order with its current status]
    D -->|Different items| X[Reject request]
    D -->|No| F{Products exist and stock is sufficient?}
    F -->|No| Y[Reject; transaction rolls back]
    F -->|Yes| G[Begin SQL transaction]
    G --> H[Reduce stock]
    H --> I[Save order and purchase-price snapshots]
    I --> J[Save OrderCreated Outbox event]
    J --> K[Commit: order PendingPayment]
    K --> L{What happens next?}
    L -->|Payment attempt| P[See payment flow below]
    L -->|Cancel with no unresolved payment| M[Restore stock; order Cancelled; OrderCancelled event]
    L -->|15 minutes pass with no unresolved payment| N[Expiration worker restores stock; order Cancelled; OrderCancelled event]
```

Order creation saves the stock change, order, and Outbox event in one SQL transaction. An early failure rolls the transaction back. A `Pending` or `Unknown` payment blocks cancellation and expiration. The expiration worker checks eligible orders every minute. See [OrderService](../Services/OrderService.cs) and [OrderExpirationWorker](../Services/OrderExpirationWorker.cs).

## Payment attempts and outcomes

```mermaid
flowchart TD
    A[POST /orders/id/payments with key] --> B{Same key already used?}
    B -->|Yes| C[Return existing payment]
    B -->|No| D{Order still PendingPayment?}
    D -->|No| X[Reject]
    D -->|Yes| E{Another payment Pending or Unknown?}
    E -->|Yes| Y[Reject new attempt]
    E -->|No| F[Create Pending payment using saved order prices]
    F --> G{Simulated outcome}
    G -->|Success| H[Payment Succeeded; order Paid; OrderPaid event]
    G -->|Failure| I[Payment Failed; order stays PendingPayment; PaymentFailed event]
    G -->|Timeout| J[Payment Unknown; outcome unresolved]
    I --> K[New payment attempt may be created]
    J -->|Later succeeds| H
    J -->|Later fails| I
```

`Unknown` means the result is unresolved. It blocks a different payment attempt, cancellation, and expiration until it becomes `Succeeded` or `Failed`. A failed payment can be followed by a new attempt while the order remains `PendingPayment`. Payment creation has an HTTP endpoint; the outcome methods are service-level simulations. See [PaymentService](../Services/PaymentService.cs).

## Refunds after successful payment

```mermaid
flowchart TD
    A[Paid order and Succeeded payment] --> B[Create refund with amount and key]
    B --> C{Same key already used?}
    C -->|Same amount| D[Return existing refund]
    C -->|Different amount| X[Reject]
    C -->|No| E{Another refund Pending or Unknown?}
    E -->|Yes| Y[Reject for now]
    E -->|No| F{Amount within remaining refundable balance?}
    F -->|No| Z[Reject]
    F -->|Yes| G[Create Pending refund]
    G --> H{Simulated outcome}
    H -->|Success| I[Refund Succeeded; amount counts toward refunded total; RefundSucceeded event]
    H -->|Failure| J[Refund Failed; amount can be tried again; RefundFailed event]
    H -->|Timeout| K[Refund Unknown; wait for resolution; RefundUnknown event]
    K -->|Later succeeds| I
    K -->|Later fails| J
    I --> L{Balance remains?}
    L -->|Yes| B
```

Refunds can be partial. Successful refunds reduce the remaining refundable amount; failed refunds do not. Refunding does not change the order's `Paid` status or restore stock. Refund creation and outcomes are service-level operations without public refund HTTP endpoints. See [RefundService](../Services/RefundService.cs).

## Outbox, RabbitMQ, retries, and product search

```mermaid
flowchart TD
    A[Order, payment, refund, or catalog change] --> B[Save Outbox event in SQL Server]
    B --> C[Outbox worker publishes to RabbitMQ]
    C --> D{Broker confirms publication?}
    D -->|Yes| E[Mark SQL Outbox event published]
    D -->|Delivered| G[RabbitMQ consumer receives event]
    D -->|Broker unavailable| F[Keep SQL event and retry later]
    F --> C
    D -->|Other publishing error| O{Fifth publishing failure?}
    O -->|No| F
    O -->|Yes| Q[Mark SQL Outbox event dead-lettered]
    G --> H{Event ID already processed?}
    H -->|Yes: duplicate| I[Acknowledge without repeating work]
    H -->|No| J{ProductUpserted?}
    J -->|Yes| K[Index versioned product snapshot in Elasticsearch]
    J -->|No| L[Record processed ID only]
    K --> L
    L --> I
    G -->|Temporary database outage| R[Retry queue: wait about 2 seconds]
    K -->|Temporary Elasticsearch outage| R
    R --> G
    G -->|Other processing error| S{Fifth failed processing attempt?}
    K -->|Other indexing error| S
    S -->|No| R
    S -->|Yes| T[RabbitMQ dead-letter queue]
```

The SQL Outbox dead-letter flag and the RabbitMQ dead-letter queue are separate. Broker outages keep Outbox publication retryable beyond five attempts. Temporary database and Elasticsearch outages do not consume the consumer's five-attempt limit. For other consumer failures, the fifth **total failed processing attempt** moves the event to RabbitMQ's dead-letter queue. A crash after broker confirmation but before marking the Outbox row published can deliver the same event again; `ProcessedMessages` prevents repeated consumer work. Product indexing also uses product ID and version to tolerate replay or out-of-order events. See [OutboxDispatcher](../Services/OutboxDispatcher.cs), [RabbitMqConsumerWorker](../Services/RabbitMqConsumerWorker.cs), and [EventConsumer](../Services/EventConsumer.cs).

Product search follows a separate read path:

```mermaid
flowchart LR
    A[GET /products/search?q=...] --> B[ProductSearchService]
    B --> C[(Elasticsearch product index)]
    C -->|Matches or no matches| D[HTTP 200: up to 20 products]
    C -->|Unavailable| E[HTTP 503]
```

Catalog creation and updates through `ProductCatalogService` create `ProductUpserted` Outbox events. The consumer copies these snapshots into Elasticsearch, so search can briefly lag behind SQL Server. Checkout always checks current price and stock in SQL Server. Other event types currently result only in a processed-message marker; they do not trigger fulfillment or email. See [ProductCatalogService](../Services/ProductCatalogService.cs), [ProductSearchService](../Search/ProductSearchService.cs), and [ProductEndpoints](../Endpoints/ProductEndpoints.cs).
