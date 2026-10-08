# Catalog, React storefront, and observability design

## Intent and agreed direction

The user requested product details and sorting, admin product management, metrics/tracing, and a storefront. They want a simple UI for demonstrating the backend, explicitly chose React and TypeScript, and approved Vite development with production assets served by the existing ASP.NET application. Success is a usable browser flow over the real APIs, protected catalog editing, correct sorted/cached search, and observable request-to-background-event processing.

Work in `/Users/kaizhi/Documents/dot-beginner/Ecommerce`, which contains the accepted earlier features. Preserve its uncommitted changes and the previous fulfillment/returns baseline and ledger. The chat worktree is older. This spec is the next review artifact; product implementation begins after written-spec and implementation-plan approval.

## Approach and delivery boundaries

Extend the existing backend and add `frontend/` containing React, TypeScript, Vite, ordinary CSS, typed API calls, and focused components. Docker adds a Node build stage, then copies the compiled assets into ASP.NET's published wwwroot. The running storefront and APIs share `http://127.0.0.1:5088`. Vite development uses a loopback server and proxies the existing API prefixes to the backend.

Use hash-based frontend routes (`/#/products/1`, `/#/cart`, `/#/orders`, `/#/admin/products`) so API paths and their 404s cannot be captured by an SPA fallback. ASP.NET serves only the built root/static assets; missing API routes keep their existing status codes. Frontend development/build must not recursively become ASP.NET project content. Node_modules, generated frontend output, test reports, and execution workspaces stay out of Git and Docker build context; the Docker frontend stage builds assets from source and the lockfile. Node is needed at build/development time, not in the final runtime image.

Three implementation phases form one integration: catalog read/admin contracts; backend telemetry and local viewer; storefront and end-to-end verification. Do not introduce a separate frontend production service, nginx, SSR, payment provider, carrier integration, product images, variants, product deletion, stock editing, shipping addresses, or new shipment/return admin screens. Existing shipment/return APIs remain available and the customer UI uses their reads/request flow.

## Public catalog contracts

Add anonymous `GET /products/{productId:int}` backed by current SQL, returning id, name, description, category, priceCents, available, and currency (EUR under the existing catalog/order model). Missing/nonpositive IDs return 404. Stock is a current observation; checkout still validates/reserves authoritative SQL stock. Detail reads do not require Elasticsearch/Redis. Use no-store so availability is not served stale by browsers.

A storefront needs browsing before the user enters text. Add anonymous `GET /products` for SQL catalog browsing, with optional category/minPriceCents/maxPriceCents, page and pageSize, and sort. Response is `{products,total,page,pageSize}`, with public summaries excluding internal version and stock; obtain availability from details. Browse sort values: idAsc (default), priceAsc, priceDesc. Category semantics match search (trimmed, exact case-sensitive); price bounds are inclusive nonnegative integer cents. Use a deliberate SQL case-sensitive comparison/collation, not the database's default case-insensitive equality, and verify index/query behavior. Pagination defaults to 1/20, size 1–50, positive page, and the same 10,000-result window as search; invalid inputs return 400, empty pages 200. Counts/pages are not a snapshot across concurrent catalog edits. Apply the existing anonymous catalog/search rate policy to browsing and detail routes; existing search behavior remains compatible.

Extend `GET /products/search` with optional case-sensitive sort=relevance|priceAsc|priceDesc; omitted sort is relevance. Preserve required nonblank q, all existing filter semantics, shape, and pagination limits. Elasticsearch orders relevance by score descending then product ID ascending; price ascending/descending uses priceCents followed by ID ascending. The final tie-breaker is deterministic.

Include normalized sort in ProductSearchParameters and its hashed Redis cache identity. Bump the cache-key schema version so old cached payloads cannot collide. Preserve shared generation invalidation, 30-second TTL, fallback on Redis outage, Elasticsearch failure behavior, and versioned asynchronous product synchronization. Test equal-price ties and both cache HIT/MISS paths for different sorts.

## Catalog administration

Introduce CatalogAdmin policy requiring `permission=products:manage`. Add `--grant-product-admin REGISTERED_EMAIL` through the existing operator-only AdminPermissionGrant helper, requiring an existing account, repeat-safe grants, exact CLI argument validation, and fresh login. Shipment/return permissions do not imply catalog permission, and catalog permission does not imply either of them. No public grant route is added.

| Route | Contract |
| --- | --- |
| GET /admin/products | SQL list; optional trimmed name substring (case-insensitive, max 200) and exact category (case-sensitive, max 200), blanks mean absent; page/pageSize; default 1/20, minima 1, cap 50, long overflow yields empty; ID ascending; response `{products,page,pageSize,total}` |
| POST /admin/products | Body name, description, category, priceCents, available; create through ProductCatalogService; 201 with product detail plus version |
| PUT /admin/products/{id:int} | Body name, description, category, priceCents, expectedVersion; update existing details only; 200 with new version, 404 missing, 409 stale version |

All routes enforce server authorization and no-store. Anonymous=401; signed-in without claim=403. Invalid fields=400. Required name/category are trimmed, 1–200 characters; description trimmed, 0–4,000 characters; priceCents nonnegative Int64; initial stock nonnegative Int32. Reject null text fields/body where required rather than producing 500. Use the same validation in HTTP and catalog service so CLI/catalog paths agree.

Reuse ProductCatalogService and its atomic ProductUpserted Outbox event. Add an optional expected-version service interface for HTTP updates while preserving CLI callers. Check the expected version under the existing EF concurrency token; catch concurrent DbUpdateConcurrencyException and map to 409. A stale update must save neither product nor event, while a successful update advances version and publishes exactly one saved snapshot. An update never writes Available, so a concurrent order stock reservation is preserved. Product creation accepts initial stock; later replenishment remains outside this scope. No additional product schema is needed solely for these APIs.

Search updates asynchronously after admin writes. The UI reflects the SQL response immediately and tells the operator search synchronization may take a moment. It does not fabricate success when search/Redis synchronization is delayed.

Add authenticated `GET /auth/me` returning the validated token's userId, email and distinct sorted permission values; no-store. This supports UI navigation with Identity's opaque bearer tokens. It grants no permissions and exposes no other user's data. Old tokens retain their old claims; UI instructs the operator to log in again after a grant.

## React UI and session behavior

Use a small responsive layout with catalog navigation, login/logout, cart, orders, and an admin link only for the catalog permission. Product cards show name/category/price and a details link; use CSS/text placeholders since there is no image model. Handle keyboard access, labels, focus, loading, empty states, and readable errors. Do not expose backend internals in shopping flows.

Views:
- Catalog: browse by default; text search on submission; category and price filters, sort and pagination; reset page on query/filter/sort changes. Abort/ignore outdated requests so a late response cannot overwrite newer selections.
- Details: SQL description, price, availability, quantity, and add-to-cart. Guest users are prompted to log in. Availability does not promise a reservation.
- Account: register/login using existing Identity endpoints; get /auth/me after login; clear session/private view caches on logout or 401. Bearer/refresh tokens remain in memory; never persist them in localStorage/sessionStorage, URLs, logs, or telemetry. Reload requires login; this is an intentional simple demo session.
- Cart: load existing Redis item quantities, hydrate product details with bounded parallel reads, edit/remove/clear items, display current estimated totals, and check out. A missing product blocks checkout with a clear message. SQL order prices returned after checkout are authoritative.
- Orders: paginated owner history and detail, immutable purchased item prices, eligible cancellation, create payment attempt, tracking/timeline, request/read return, and payment/refund status. Add owner-only `GET /orders/{id}/payments` to recover existing payment attempts after navigation/relogin; no-store, newest CreatedAt/Id descending, page default 1 and size default 20/capped 50/minima 1, long-offset overflow empty, same ownership privacy as payment reads. This avoids creating a new payment just to discover an existing one.
- Admin catalog: paginated/searchable list, create form including initial stock, edit form excluding stock, version-aware save, stale-edit notice with explicit reload. The server remains the authority even if a user manually navigates to the admin hash route.

Typed fetch handling supports JSON errors, Identity validation errors, empty bodies, generic failures, and 429 Retry-After. Prices use integer cents; validate safe integer representation in the browser and show an unsupported-value error rather than rounding an oversized JSON number. Decimal input converts by splitting decimal digits, avoiding floating-point multiplication. Existing API numeric contracts are preserved.

Keep operation idempotency keys stable for retries after network uncertainty. Store only non-secret operation identifiers/keys in sessionStorage, namespaced by authenticated user/order, never bearer tokens or private response payloads. An uncertain checkout/payment/return attempt has a retry action using its original key; it must not silently generate a new key. After a confirmed success, a new user-requested operation gets a new key. Disable duplicate submissions while pending, but preserve keys when requests fail or return 429/503. A replay can still work if the cart changes or expires.

Checkout currently preserves the cart. The UI must not automatically delete it after ordering because that could erase concurrent edits; show the saved order plus an explicit Clear cart action. Return reasons are entered by the customer, and the UI shows Requested/Approved/Received/Completed and live financial amounts without inventing warehouse actions or restocking.

Add public no-store `GET /ui/config` with only `paymentSimulationEnabled` reflecting Development. In default mode checkout creates a Pending payment, and the UI clearly reports that no real payment provider is connected. In Development, show an explicitly labelled local payment-outcome simulator for the owner using existing /dev routes; never claim a real charge. Refund outcome simulation remains in the existing documented HTTP walkthrough; the storefront reads the resulting refund/return state. Default simulator routes remain absent. Configuration does not expose connection strings, credentials or admin identities.

## Metrics and distributed tracing

Use System.Diagnostics ActivitySource/Meter with the OpenTelemetry .NET SDK. Register ASP.NET request and outgoing HTTP instrumentation, runtime metrics, and SQL client spans with SQL text/parameters disabled. Instrument Redis cache/cart and Elasticsearch boundaries with small custom spans where a safe automatic instrumenter is unavailable. The requirement is visible dependency spans and durations, not an exporter for every library. Keep trace bodies, credentials, auth headers, cart keys, raw search queries, addresses and reasons out of telemetry.

Add custom bounded-cardinality metrics for search cache hit/miss/error, dependency operation duration/failures, checkout/payment/refund/return outcomes, Outbox publish outcomes/retries and consumer handled/duplicate/failure outcomes. Tags use fixed operation, result, dependency and known event type values; never user/product/order/message IDs, search text, raw exception messages or arbitrary event names as metric labels. Built-in HTTP metrics use route templates. Logs retain trace/span correlation without request/response bodies or secrets. Filter/sanitize automatic instrumentation attributes so full URLs/query strings, SQL statements and connection strings are not exported.

Carry trace context across the durable Outbox boundary: nullable TraceParent (max 55, W3C version 00)/TraceState (max 512) columns captured for newly added Outbox messages from Activity.Current during SQL save, without changing message identity, payload, business transaction or replay semantics. Update all SaveChanges overloads used by the app consistently or centralize an EF interceptor. Never overwrite stored origin context on retry. Extend BrokerEvent with optional backward-compatible context fields; publisher exports a producer span using the saved origin, carries its current context in the broker envelope, and consumers start a Consumer span from validated context. Malformed/missing context starts a fresh trace without failing otherwise valid business messages. Retry/dead-letter routing retains the envelope/context. Instrument consumer spans around the existing atomic handler, not an extra business transaction. Test a request-created event, delayed dispatch, consumption, duplicate and retry correlation. Old rows/events without trace fields continue working.

Use OTLP export only when configured; a missing/unavailable viewer must not fail startup, readiness, checkout or background processing. No external SaaS/account is required. Add optional `compose.observability.yaml` with the standalone Aspire dashboard: browser UI loopback port 18888; OTLP internal network endpoints, no publicly exposed ingest port. Retain dashboard browser-token authentication, obtain its login URL from operator container logs, and document telemetry as a local diagnostic tool. Pin a stable supported image during implementation. Configure API service.name=ecommerce and OTLP endpoint to the dashboard. The default five-service stack continues without the optional viewer; the observability overlay has six services. No Aspire AppHost or orchestrator migration is needed.

Browser-side OpenTelemetry is outside the initial scope; backend server traces start at incoming requests, which is sufficient for this demo. Dashboard data is diagnostic, not an audit/financial ledger. Do not add dependencies on telemetry to business correctness or store telemetry on commerce tables beyond optional propagation fields.

## Verification and integration

Backend tests prove public detail SQL freshness/404, browsing filters/paging/ties, all search sort modes and distinct cache identities, invalid input, permission separation, existing-account grants/frozen claims, atomic catalog/Outbox rollback, expected-version conflict and stock preservation during concurrent editing/checkout. Add SQL migration upgrade tests for old Outbox rows and broker compatibility without context.

Telemetry tests use in-memory/test exporters or ActivityListener/MeterListener: expected spans and counters, bounded/sanitized tags, sampled/delayed parent propagation, malformed context resilience, and viewer outage without business failures. Optional real OTLP smoke checks demonstrate HTTP→SQL/cache/search and HTTP→Outbox→RabbitMQ→consumer trace correlation in the dashboard. Verify existing broker recovery/deduplication and generation invalidation still hold.

Frontend checks include TypeScript compilation, production build, focused component/API tests for stale response handling, login/private-cache reset, 401/403/429 errors, stable retry keys and edit conflicts. Browser end-to-end tests exercise real default browse/login/cart/checkout/order detail and admin denial; Development adds paid shipment/tracking/return creation using fresh test accounts/products. Catalog-admin tests grant only fresh fixture accounts through CLI and verify SQL-to-search synchronization. A desktop/mobile visual pass verifies the simple UI and navigation. No mock checkout replaces the real API in the end-to-end tests.

Docker/CI build frontend before publishing, test static root/assets while preserving unknown API 404s and disabled default simulators, and run existing service/SQL/HTTP/broker regressions with the existing consumer isolation procedure. Restore the default API after Development/observability/outage checks, preserve application data, and document retained test fixtures. README/API/Docker/frontend/observability guides explain run commands, catalog grant, fresh login, asynchronous search, demo payment behavior, telemetry viewer and supported limitations.

## Sources consulted

- [Vite guide](https://vite.dev/guide/): React/TypeScript template and current Node requirements; choose a supported pinned toolchain/lockfile during implementation.
- [Standalone Aspire dashboard](https://aspire.dev/dashboard/standalone/): supports local logs/traces/metrics via OTLP while retaining Docker Compose; container browser UI is 18888 and ingest is 18889 gRPC/18890 HTTP, with browser-token authentication by default.
- [Microsoft OTLP example](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example): .NET SDK/exporter and custom instrumentation integration.
- [OpenTelemetry .NET instrumentation](https://opentelemetry.io/docs/languages/dotnet/instrumentation/): ActivitySource and Meter instrumentation conventions.

## Review conditions

This spec assumes EUR catalog display, in-memory browser login, no stock-edit/delete APIs, hash-based React navigation, an optional local telemetry viewer, and Development-only simulated payment outcomes. These choices keep the user's simple-UI goal concrete. Review this written spec before creating the implementation plan. The plan should use inline execution for the dependent integration, preserve the current baseline, and end with one independent review focused on private UI state, uncertain-write replay, concurrent stock/catalog updates, and durable trace propagation without business regressions.
