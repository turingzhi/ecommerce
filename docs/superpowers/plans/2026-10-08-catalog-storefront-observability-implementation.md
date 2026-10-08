# Catalog, Storefront, and Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver SQL product browsing/details, sorted cached search, protected catalog management, backend metrics/distributed traces, and a simple React/TypeScript storefront over the real commerce APIs.

**Architecture:** Reuse ProductCatalogService, Identity permissions, SQL/Outbox, Elasticsearch and Redis. Build a React/Vite application under frontend and publish it as ASP.NET static assets with hash navigation. An optional standalone Aspire dashboard receives backend OpenTelemetry without becoming a business dependency.

**Tech Stack:** Existing .NET 10/EF SQL Server/Identity/Elastic 9.5.2/RabbitMQ 7.2.2/Redis 3.3.1, React/TypeScript/Vite, OpenTelemetry .NET, xUnit, Vitest/Testing Library, Playwright and Docker Compose. Resolve and pin supported frontend/telemetry package versions with committed lockfiles during execution; use a supported Node 22 release >=22.12 in development/CI/Docker.

**Spec:** [Approved catalog, React storefront and observability design](../specs/2026-10-08-catalog-storefront-observability-design.md).

## Global Constraints

- Implement in `/Users/kaizhi/Documents/dot-beginner/Ecommerce`. Preserve all accepted uncommitted work and previous fulfillment/returns execution artifacts. Record exact tracked/untracked baseline contents in this plan's ignored workspace before edits. Stage only coherent feature files/hunks; defer mixed commits when prior features cannot be separated safely.
- The three milestones are independently testable checkpoints. Telemetry could be executed as its own plan; keep this coordinated plan because all requested behavior must integrate with the same API, durable event schema and final frontend Docker image.
- Frontend production files share `http://127.0.0.1:5088` with APIs. Hash navigation avoids API fallback routing. No separate frontend runtime, nginx, SSR, product images/variants/deletion, stock adjustments, carrier/provider/address integration or new shipment/return admin UI.
- Public browse/search use page 1/size 20, positive page/size 1–50, and page*pageSize <=10,000. Public browse sort idAsc|priceAsc|priceDesc; search sort relevance|priceAsc|priceDesc. Case-sensitive exact category after trimming, nonnegative inclusive Int64 prices, ID ascending tie-breaker. Preserve required q for search. Bump search cache keys to v3, retain 30-second TTL and shared generation invalidation.
- Catalog admin uses `permission=products:manage`, existing-account-only `--grant-product-admin EMAIL`, fresh login and no implication between catalog/shipment/return claims. Admin paging minima 1, size default20/cap50, long-overflow empty. Private responses and public stock detail/config use no-store.
- Catalog text limits name/category1–200 and description0–4,000, trimmed/non-null; price nonnegative Int64; initial stock nonnegative Int32. HTTP update requires expectedVersion, preserves stock and writes exactly one atomic ProductUpserted snapshot. Existing CLI signatures stay supported.
- Browser bearer/refresh tokens stay in memory; logout/401 invalidates private state and reload requires login. Only namespaced non-secret operation keys/IDs may enter sessionStorage. Keys survive uncertain checkout/payment/return retries. Cart is never automatically cleared after checkout. Unsupported monetary integers must not silently round.
- Default payments remain Pending with a clear no-provider message; only Development exposes the labelled existing payment simulator. Refund simulation stays in HTTP walkthroughs. GET /ui/config exposes only paymentSimulationEnabled.
- Backend telemetry excludes secrets, bodies, raw query text/SQL, personal details and Redis keys. Metric tags are bounded operation/result/dependency/event types, not IDs. Viewer absence/outage must not fail startup/readiness/commerce. Default stack has five services; optional observability overlay six, dashboard UI loopback18888, ingest internal only, browser-token auth retained.
- Nullable Outbox TraceParent max55 (W3C00) and TraceState max512 survive durable dispatch; never change event identity/payload or overwrite origin on retry. Old/malformed context cannot reject a valid event. Preserve ACK/deduplication/retry/dead-letter behavior.
- Use isolated GUID verification databases, clean in finally, never print credentials. Drain queues and stop API for real isolated broker runners; restore default API in finally after Development/viewer/outage checks. Never delete application volumes/data.

## Review Focus

1. A slower request from customer A finishes after logout/login as B: no private state or protected action should appear for B (Task 8).
2. A successful checkout loses its response and the cart is edited before retry: retry must use the original key and return the saved order without clearing edits (Task 9).
3. Catalog editing overlaps SQL stock reservation: stock and purchase prices must remain correct; a losing version update must not leave an Outbox event (Task 3).
4. An unsampled request's event is dispatched much later or retried: valid durable correlation must survive without making telemetry mandatory or rejecting an older event (Tasks 5/6).
5. A frontend hash route, missing API, or missing asset is requested in the published image: storefront navigation works while backend failures remain real 404/401/403, never index.html (Task 7).

## File responsibilities and dependency map

- Public contracts: Dtos/ProductDetailsResponse.cs, Search/ProductBrowseParameters.cs, Services/ProductQueryService.cs, Endpoints/ProductEndpoints.cs. SQL reads remain separate from cached Elasticsearch search.
- Search sorting: ProductSearchParameters.cs/ProductSearchService.cs, existing tests and new real sorting/cache HTTP checks.
- Administration: Dtos/CatalogAdminRequests.cs, Services/ProductAdministration.cs, existing ProductCatalogService/AdminPermissionGrant, Endpoints/ProductAdminEndpoints.cs, DTO/list helpers and SQL/HTTP verification.
- UI support: Dtos/CurrentUserResponse.cs/PaymentListResponse.cs, Endpoints/AuthEndpoints.cs/PaymentEndpoints.cs/UiEndpoints.cs.
- Telemetry: Observability/CommerceTelemetry.cs, TelemetryRegistration.cs, TelemetrySanitizer.cs, OutboxTraceCapture.cs; existing ShopDb/Outbox/BrokerEvent/publisher/consumer/dependency boundaries; focused listener/SQL/broker tests.
- Frontend: typed API client and DTOs, auth session context, operation-key store, request lifecycle hooks, catalog/cart/orders/admin feature components, simple CSS and hash routes. Tests live next to frontend responsibilities and e2e flows under frontend/e2e.
- Integration: Dockerfile, Ecommerce.csproj, .dockerignore/.gitignore, CI, compose.observability.yaml, guides and editable HTTP examples. Generated frontend output is not source-controlled.

Milestone A: Tasks1–4 provide complete catalog/admin/UI-support APIs. Milestone B: Tasks5–6 provide measurable backend behavior and durable traces plus optional viewer. Milestone C: Tasks7–11 deliver the storefront and integration; Task12 verifies all milestones and performs one fresh review.

### Task 1: SQL product browsing and detail reads

**Create:** Dtos/ProductDetailsResponse.cs, Search/ProductBrowseParameters.cs, Services/ProductQueryService.cs, Verification/CatalogVerification.cs, Ecommerce.Tests/ProductBrowseParametersTests.cs, scripts/verify_catalog_http.py.
**Modify:** Endpoints/ProductEndpoints.cs, Program.cs, docs/api.md, Http/products.http.

**Interfaces:**
- ProductDetailsResponse(Id:int,Name:string,Description:string,Category:string,PriceCents:long,Available:int,Currency:string), static From(Product) uses EUR.
- ProductBrowseParameters(Category:string?,MinPriceCents:long?,MaxPriceCents:long?,Page:int,PageSize:int,Sort:string); Offset:int. TryCreate(string? category,long? min,long? max,int? page,int? pageSize,string? sort,out ProductBrowseParameters?,out string?):bool.
- ProductQueryService(ShopDb).GetAsync(int id,CancellationToken):Task<ProductDetailsResponse?>; BrowseAsync(ProductBrowseParameters,CancellationToken):Task<ProductSearchPageResponse>. Reuse existing ProductSearchResponse for summaries.
- Verification.RunCatalog():Task; Program dispatch --verify-catalog before hosting; independent GUID SQL database with migration/finally cleanup. SQL checkout/catalog race assertions will be added in Task3.

- [ ] Write parser tests first: default sort idAsc/page1/size20, trimmed/blank category, inclusive bounds, reject size0/51, page0, 10,001-result window, invalid sort and negative/reversed price. Run `dotnet test Ecommerce.Tests --filter ProductBrowseParametersTests`; expect missing type/method RED.
- [ ] Write VerifyCatalogReadsAsync(options): fixed products Alpha/alpha category distinction, equal prices, prices1000/2000/3000. Assert category exact, priceAsc/Desc with equal-price IDs asc, correct total/disjoint pages, detail current stock after direct SQL update, missing/nonpositive null. Observe --verify-catalog RED before implementing query service.
- [ ] Implement interfaces using AsNoTracking SQL reads and explicit SQL Server case-sensitive category collation. Select detail directly; public browse count/filter/order/paging returns summaries. No detail dependency on ES/Redis; no product schema changes.
- [ ] Map GET /products and /products/{productId:int}, anonymous with existing SearchPolicy; invalid browse400, missing detail404, detail no-store, cancellation. Register service/CLI. Add HTTP checks anonymous detail, filters/bounds/ties, nonexistent404 and SQL freshness.
- [ ] Verify parser units, --verify-catalog and HTTP after combined catalog rebuild; docs describe browse versus required-q search. Record checkpoint; commit only separable changes or ledger why mixed commits are deferred.

### Task 2: Elasticsearch sorting and cache identity

**Modify:** Search/ProductSearchParameters.cs, Search/ProductSearchService.cs, Endpoints/ProductEndpoints.cs, Ecommerce.Tests/ProductSearchParametersTests.cs, scripts/verify_search_http.py, Verification/ProductSyncVerification.cs, docs/redis.md/docs/api.md.
**Create:** scripts/verify_product_sort_http.py.

**Interfaces:**
- Extend ProductSearchParameters with trailing `string Sort="relevance"` so existing constructors work. Preserve old TryCreate(q,category,min,max,page,size,out value,out error), delegating to a new overload with string? sort before out parameters. Normalize null to relevance; reject empty/unknown/wrong-case sort.
- SearchAsync(ProductSearchParameters,CancellationToken):Task<ProductSearchResult> unchanged. CacheKey(generation) returns products:search:v3:generation:SHA256(normalized JSON including Sort).

- [ ] Add literal unit assertions: omitted/relevance share key, priceAsc and priceDesc each differ, all keys begin products:search:v3:3:, unknown sort rejected. Run selected units RED, preserving previous parameter tests.
- [ ] Add real Elastic HTTP fixtures through catalog CLI with equal-price product names containing unique text. Assert priceAsc=[1000-IDa,1000-IDb,2000], priceDesc=[2000,1000-IDa,1000-IDb], relevance preserved, filters/pages/totals unchanged. Separate identical-parameter sorts must MISS then HIT their own keys; after update/generation change all modes show the newer data. Observe behavior RED before implementation.
- [ ] Implement normalized sort and corresponding Elastic sort descriptors; ID asc is always final tie-breaker. Retain exact-integer range bounds and no partial/timed-out responses. Endpoint passes sort into new parser and preserves Redis-outage fallback/TTL.
- [ ] Run full units, existing search and new sorting HTTP checks after rebuilt catalog API; --verify-product-sync runs in Task12 with API consumer paused. Document async refresh/key version. Checkpoint scoped diff.

### Task 3: Version-aware catalog administration

**Create:** Dtos/CatalogAdminRequests.cs, Dtos/AdminProductResponse.cs, Services/ProductAdministration.cs, Endpoints/ProductAdminEndpoints.cs, Verification/CatalogAdminVerification.cs, scripts/verify_catalog_admin_http.py.
**Modify:** Services/ProductCatalogService.cs, ProductQueryService.cs, Program.cs, docs/api.md/docs/docker.md, Http/products.http.

**Interfaces:**
- CreateCatalogProduct(string? Name,string? Description,string? Category,long PriceCents,int Available); UpdateCatalogProduct(string? Name,string? Description,string? Category,long PriceCents,long ExpectedVersion).
- AdminProductResponse(Id:int,Name:string,Description:string,Category:string,PriceCents:long,Available:int,Currency:string,Version:long), static From(Product); AdminProductListResponse(Products:IReadOnlyList<AdminProductResponse>,Page:int,PageSize:int,Total:long).
- ProductAdministration.Policy="CatalogAdmin", Permission="products:manage", GrantAsync(UserManager<IdentityUser>,string):Task calls existing AdminPermissionGrant.
- Keep CreateAsync(ProductChange,CancellationToken) and UpdateAsync(int,ProductDetailsChange,CancellationToken). Add UpdateVersionedAsync(int,ProductDetailsChange,long expectedVersion,CancellationToken):Task<Product> sharing validation/update body; ArgumentException invalid, KeyNotFoundException missing, DbUpdateConcurrencyException stale/competing. HTTP requires positive expectedVersion.
- ProductQueryService.ListForAdminAsync(string? name,string? category,PageBounds,CancellationToken):Task<AdminProductListResponse>; filters trimmed, max200, name case-insensitive substring, category exact case-sensitive; ID asc, total count even when overflow empty.

- [ ] Write SQL validation/rollback/version tests: trim valid boundary text; reject null/empty/201-char name/category, null/4001 description, negative price/stock; stale expectedVersion leaves product/Outbox unchanged; two edits same version one200/one409 with one new snapshot. Run RED with missing version method/unchanged validation.
- [ ] Pin stock race with coordinated separate contexts: pause catalog update after load; reserve stock via existing OrderService; release edit. Assert stock1 from fixture2, order saved checkout price, edit never restores stock. If edit loses version race it writes no event; if succeeds one event matches saved details. Force Outbox insertion failure and assert product creation/update rolls back; retry after removal succeeds.
- [ ] Implement service validation/version path without updating Available. Preserve CLI signatures and its no-stock-update rule. Map protected GET/POST/PUT routes and status codes; all normal/error responses no-store, no public grant. Register policy/service/CLI exact arguments before hosting/seed.
- [ ] HTTP tests: anonymous401, customer403, shipment-only/return-only403 including known IDs, missing account fails, repeated grant one claim, old token403/fresh token works, invalid400/missing404/stale409; legitimate create201/update200, filter/paging and stock preserved. Verify async created/edited product appears in every search sort using existing Outbox path.
- [ ] Run unit/--verify-catalog and catalog admin HTTP. Record A milestone progress; preserve preceding features and use scoped checkpoint.

### Task 4: Auth identity, UI config, payment recovery

**Create:** Dtos/CurrentUserResponse.cs, Dtos/PaymentListResponse.cs, Endpoints/UiEndpoints.cs, scripts/verify_storefront_support_http.py.
**Modify:** AuthEndpoints.cs, PaymentEndpoints.cs, Program.cs, docs/api.md.

**Interfaces:**
- CurrentUserResponse(UserId:string,Email:string?,Permissions:IReadOnlyList<string>) from authenticated claims; permission values distinct and ordinal sorted.
- GET /auth/me authenticated/no-store: missing NameIdentifier401; otherwise current token claims only. No opaque-token decoding in browser.
- PaymentListResponse(Page:int,PageSize:int,Payments:IReadOnlyList<PaymentResponse>); GET /orders/{orderId:guid}/payments authenticated owner: order existence/ownership checked before list, 404 otherwise, default1/20, min1/max50, overflow empty, CreatedAt/Id desc, no-store/no write quota.
- GET /ui/config anonymous/no-store: `{paymentSimulationEnabled:bool}` from IsDevelopment only.

- [ ] Add HTTP RED for new routes; assert anonymous me/payments401, other owner's payment list404, unknownorder404, empty owned list200, fresh/old permission snapshot and deterministic permission ordering. Config exposes exactly one boolean; defaultfalse and Developmenttrue.
- [ ] Implement three handlers/register without changing existing Identity/login/payment behavior. Use PageBounds for payment recovery and existing PaymentResponse.From UTC serialization. Read quotas remain unaffected.
- [ ] Verify default/development support HTTP and existing payment/financial reads. Update docs and record complete catalog/API milestone.

### Task 5: Safe backend metrics and dependency spans

**Create:** Observability/CommerceTelemetry.cs, TelemetryRegistration.cs, TelemetrySanitizer.cs, Ecommerce.Tests/CommerceTelemetryTests.cs, TelemetrySanitizerTests.cs.
**Modify:** Ecommerce.csproj, Program.cs/appsettings.json, product/cart endpoint/service boundaries, OrderService/PaymentService/RefundService/ReturnService/OutboxDispatcher/EventConsumer.

**Interfaces:**
- CommerceTelemetry.SourceName and MeterName="Ecommerce.Commerce", Meter version1.0; IDisposable operation scope StartOperation(string operation,string dependency):TelemetryOperation; RecordCache(string result):void; RecordOutcome(string operation,string result):void; RecordEvent(string operation,string type,string result):void. TelemetryOperation is a sealed IDisposable with Complete(string result):void and Dispose():void; it records a fixed outcome/duration exactly once. Dispose without Complete records failure unless Complete("cancelled") was called. Normalize unknown operation/dependency/result inputs to other before recording tags; never accept arbitrary labels.
- Allowed operations include search,cart.read/cart.write,checkout,payment.create/payment.outcome,refund.create/refund.outcome,return.create/return.update,catalog.create/catalog.update,outbox.publish,event.consume. Dependency tags sqlserver/redis/elasticsearch/rabbitmq/internal. Results success/replayed/conflict/invalid/not_found/unavailable/cancelled/failure plus event handled/duplicate/retry/dead_letter. Map unknown event types to other.
- Meter instruments ecommerce.search.cache.requests (counter), ecommerce.operation.duration (histogram seconds), ecommerce.operation.outcomes (counter), ecommerce.messaging.outcomes (counter).
- TelemetryRegistration.AddCommerceObservability(IServiceCollection,IConfiguration):IServiceCollection registers SDK, ASP.NET/outgoing HTTP traces, runtime/ASP.NET metrics, SQL client spans, custom source/meter; OTLP exporter only if a valid optional Observability:OtlpEndpoint is set. service.name=ecommerce, W3C activities; no viewer-dependent health check.
- TelemetrySanitizer.Apply(Activity):void removes SQL text/parameters/connection strings, full URL/query, request/response bodies, auth/cookie headers and raw exception details; retain safe route/method/status, normalized dependency/operation/outcome. Register sanitizer on exported automatic/custom spans and verify instrumenter capture defaults are disabled.

- [ ] Resolve supported OpenTelemetry package versions against .NET10 and official API documentation without changing business packages; use explicit pinned versions. Document resolved versions/options in ledger. Add ActivityListener/MeterListener tests RED: cache hit/miss/error three counts; operation duration once; IDs/raw query/event names absent from metric labels; unknown operation/dependency/result/event type becomes other; sanitizer strips supplied token/query/SQL attributes while safe route/outcome remain.
- [ ] Implement custom telemetry/registration; enable export batching with no startup connection check. Disable SQL statement capture at source, avoid logging bodies/query/PII; automatic HTTP attributes sanitized. Skip noisy static/health tracing where appropriate without dropping business endpoints.
- [ ] Wrap real cache/search/cart boundaries and commerce outcomes; do not hold telemetry locks or create extra business transactions. Each actual committed result is recorded once, failed/replayed cases have fixed labels. Logs include trace/span IDs; SDK export cannot throw into commerce call sites. Cancellation is not misreported as business failure.
- [ ] Run unit listeners and existing service/SQL suites. Configure an unreachable OTLP address in a test-host smoke run and prove readiness/checkout still succeed; reset config in finally. This task is measurable without a dashboard.

### Task 6: Durable Outbox-to-RabbitMQ traces and viewer

**Create:** Observability/OutboxTraceCapture.cs, Ecommerce.Tests/OutboxTraceCaptureTests.cs, Verification/TelemetryVerification.cs, generated AddOutboxTraceContext migration, compose.observability.yaml, docs/observability.md.
**Modify:** Data/ShopDb.cs, Models/OutboxMessage.cs, Services/RabbitMqEventPublisher.cs/RabbitMqConsumerWorker.cs, Verification/RabbitMqVerification.cs, Program.cs, docs/docker.md.

**Interfaces:**
- OutboxTraceCapture.Capture(DbContext):void scans Added Outbox rows with null origin, copies valid Activity.Current.Id/TraceStateString (55/512 maxima) only once. Call through a central registered interceptor plus verification options, or ShopDb SaveChanges overloads; choose one approach covering sync/async/bool overloads with tests.
- OutboxMessage.TraceParent:string?, TraceState:string? nullable max55/512. Migration adds only two nullable columns, old rows preserved.
- Existing BrokerEvent (defined in RabbitMqEventPublisher.cs) gains trailing optional string? TraceParent=null,TraceState=null; ToOutboxMessage retains context. Existing four-argument callers remain valid.
- CommerceTelemetry.TryParseParent(string? parent,string? state,out ActivityContext context):bool rejects malformed/unsupported/oversized values. Publisher producer span uses saved context as parent (or a new root for absence), outgoing envelope uses producer context; consumer span uses envelope context. A null span when unsampled still preserves valid stored parent for propagation. Retry body/context remain intact.
- Verification.RunTelemetry():Task for --verify-telemetry; isolated SQL migration/capture tests, listeners for delayed/broker-like envelope propagation; real RabbitMQ propagation assertions extend the existing paused-API runner.

- [ ] Write capture/context RED tests: all SaveChanges overloads capture origin, subsequent retry save never changes it; no current Activity leaves null; W3C sample flag00 still parses/propagates; old four-field JSON roundtrips; malformed context does not change valid event eligibility. Test lengths exactly55/512 and oversized state safely ignored.
- [ ] Generate/review migration; implement central capture without modifying payload/message ID or ACK/transaction semantics. SQL tests migrate a pre-context Outbox row and assert existing fields survive/null context, new traced transaction persists context atomically, forced rollback leaves no message.
- [ ] Add producer/consumer spans and context fields. Listener assertions: original request TraceId equals delayed producer and consumer TraceIds, producer parent request SpanId, consumer parent producer SpanId; duplicate/retry spans keep trace and fixed result labels. Invalid context uses new root, not ambient worker/batch context. Run --verify-telemetry GREEN and old broker serializer tests.
- [ ] Add overlay with pinned stable Aspire dashboard, authenticated UI `127.0.0.1:18888:18888`, internal gRPC18889/HTTP18890; configure API OTLP endpoint http://dashboard:18889 via overlay only. No startup dependency on dashboard or exposed ingest ports. Document token login via operator logs and default five versus overlay six services.
- [ ] Start overlay; generate cache miss/hit/admin catalog/checkout events and inspect metrics plus connected trace in viewer. Stop only dashboard and prove API readiness/checkout/event processing still work; restore/remove overlay in finally. Run real broker correlation in Task12 with API paused. Record complete B milestone; no business code depends on dashboard availability.

### Task 7: React toolchain, typed API client, static hosting

**Create:** frontend/package.json/package-lock.json, tsconfig files, vite.config.ts, index.html, src/main.tsx, App.tsx, styles.css, api/client.ts/types.ts, api/client.test.ts, components/AppShell.tsx/ErrorNotice.tsx, frontend/README.md.
**Modify:** Dockerfile, Ecommerce.csproj, Program.cs, .gitignore/.dockerignore, CI.

**Interfaces:**
- RequestOptions {method?:string;body?:unknown;token?:string;key?:string;signal?:AbortSignal}; apiRequest<T>(path:string,options?:RequestOptions):Promise<T>. ApiError extends Error {status:number;retryAfterSeconds:number|null;details:Record<string,string[]>|null}. Empty204 returns undefined; error payload parsing works for Identity validation/error/ProblemDetails/plaintext fallback.
- TypeScript DTOs mirror actual camelCase backend contracts from Tasks1–4 and current order/cart/tracking/return/refund APIs. Monetary/version values are number with safe-integer validation before display/actions; no JWT assumptions.
- Hash router routes /, /products/:id, /login, /cart, /orders, /orders/:id, /admin/products and visible frontend-not-found. Vite dev loopback5173 proxy prefixes /auth,/products,/cart,/orders,/payments,/refunds,/admin,/ui,/dev to configured loopback backend5088. Port is strict; dev commands/config contain no secrets.
- npm scripts dev, build (`tsc` before Vite), test (`vitest run`), test:e2e (`playwright test`). React/TypeScript/Vite/React Router/Vitest/Testing Library/Playwright versions pinned with lockfile; browser tests use Chromium.

- [ ] Establish supported pinned Node/frontend versions and create minimal toolchain. Add API-client tests for JSON/204/errors/Retry-After/auth/key headers and abort. Observe missing-client RED, then implement typed client with no token/query/body logging. Scaffold/setup belongs to this deliverable, not separate review task.
- [ ] Build hash shell and simple responsive CSS. Update ASP.NET to serve static default files/assets only, with no SPA fallback; browser hash routes never reach backend route matching. Exclude frontend source/node_modules/generated artifacts from web project Compile/Content/None globbing.
- [ ] Docker Node22 stage runs npm ci/build; copy dist into published wwwroot after dotnet publish. .dockerignore excludes node_modules/dist/generated wwwroot/test reports/.superpowers, not frontend source/lockfile. Host build documented as npm ci/build then copy assets/publish; no automatic npm install inside dotnet test.
- [ ] Run npm test/build, dotnet build/test, Docker build. HTTP assertions root contains app entry/static bundles; /unknown-api, /products/not-an-id and missing .js remain404/non-HTML; anonymous protected routes retain401. Verify existing default simulators remain404. Record skeleton ready independently of commerce pages.

### Task 8: Login/session and product discovery

**Create:** frontend/src/auth/AuthProvider.tsx/AuthProvider.test.tsx, features/account/LoginPage.tsx, features/catalog/CatalogPage.tsx/ProductDetailsPage.tsx/catalog.test.tsx, hooks/useLatestRequest.ts/useLatestRequest.test.ts, lib/money.ts/money.test.ts.
**Modify:** App.tsx/AppShell.tsx/types.ts/styles.css.

**Interfaces:**
- AuthSession {token:string;user:CurrentUserResponse}; useAuth():{session:AuthSession|null;login(email,password):Promise<void>;register(email,password):Promise<void>;logout():void;generation:number}; token memory only. Increment generation on login/logout/401; private async completions must match captured generation and user. 403 retains session and displays permission error.
- useLatestRequest<T>(load:(signal:AbortSignal)=>Promise<T>,dependencies:unknown[]):{data:T|null;loading:boolean;error:ApiError|Error|null}; abort old requests and guard revision. Unmount cannot update state.
- parsePriceInput(value:string):number validates decimal max2 places via digit splitting/BigInt safe bound; formatMoney(cents:number,currency="EUR"):string validates safe integer. Reject negative, overflow, Infinity and excess decimals; display actionable unsupported-value message for oversized API money.

- [ ] Write focused tests RED: slow old query cannot overwrite new sort; private customer-A response after logout/loginB discarded; 401 resets token/private state, 403 does not; no token appears in browser storage. Money literals 19.99→1999, 0.01→1, 1.001/error and >MAX_SAFE_INTEGER/error.
- [ ] Implement auth/account UI from Identity endpoints then /auth/me; do not persist refresh/access token. Generation applies to every private data load/write completion, not just initial login. Hide admin navigation without products:manage and enforce denial after direct hash navigation.
- [ ] Implement catalog browse/search switch, category/price filters, sorting/default mapping (browseidAsc vs searchrelevance), pagination/page reset, stale request handling, empty/error/loading states. Detail displays current SQL availability; adding an exact quantity uses existing PUT cart semantics after login.
- [ ] Run components/API/money tests and production build. Browser smoke mobile/desktop catalog/login/details; user text rendered through React text nodes, never raw HTML. Document reload login and current-availability limits.

### Task 9: Cart, checkout and retry-safe operation keys

**Create:** frontend/src/lib/operationKeys.ts/operationKeys.test.ts, features/cart/CartPage.tsx/cart.test.tsx, hooks/usePrivateRequest.ts, e2e/cart-checkout.spec.ts.
**Modify:** API types and navigation.

**Interfaces:**
- OperationKind="checkout"|"payment"|"return". OperationKeys.getOrCreate(userId:string,kind:OperationKind,resourceId:string):string; confirm(userId,kind,resourceId):void removes completed intent; resourceId="cart" for checkout, order ID otherwise. sessionStorage namespace `ecommerce:operation:v1:userId:kind:resourceId`; values contain only key/operation ID, never responses/tokens/reasons. Existing unknown key is reused across refresh/login/edits. Explicit New checkout action resets only after confirmed resolution or explicit warning acknowledging a new independent operation.
- usePrivateRequest consumes AuthProvider generation and latest-request cancellation; it must guard mutation-success UI as well as read results.
- Cart metadata hydration uses at most five concurrent detail requests, cached per page load; missing/unsafe-price item blocks checkout with clear message. Cart quantity1–100 and max100 distinct products are server enforced; totals sum exact safe cents or display unsupported-value error.

- [ ] Write RED retry tests: mock first checkout commits but throws network error; user edits cart; next call keeps same header key and shows original returned order; no DELETE cart call. Refresh/session reset still retrieves unknown-operation key for same user; different user/order/kind gets a different namespace; 429/503 preserves key and Retry-After message.
- [ ] Implement cart reads/metadata/edit/remove/explicit clear and checkout with stable key and disabled in-flight submit. Confirm checkout success stores only the returned order in current in-memory state; create/recover its Pending payment using a separate stable payment key scoped to that order, then navigate to order. If payment creation is uncertain, retry only that payment key or recover through the payment list; never rerun checkout with a new key. Cart stays untouched. Provide explicit retry after uncertainty rather than automatic new order/key.
- [ ] Real default Playwright flow creates fresh user/product through test fixtures, adds/edits cart and checks out; inspect order SQL/current prices and retained cart. UI loginB never displays delayed A cart/order. Use real endpoints for end-to-end; mocks only unit behavior tests.
- [ ] Run frontend tests/build and real browser default checkout, existing cart checkout HTTP regressions. Record checkout ready; document preserved cart/keys.

### Task 10: Customer orders, payments, tracking and returns

**Create:** frontend/src/features/orders/OrdersPage.tsx/OrderDetailsPage.tsx/orders.test.tsx, components/StatusTimeline.tsx, e2e/order-lifecycle.spec.ts.
**Modify:** client DTOs/routes/app shell.

**Interfaces:** Consume existing OrderList/OrderResponse, new payment recovery list, PaymentResponse/RefundListResponse, OrderTrackingResponse, ReturnResponse and OperationKeys. Config type `{paymentSimulationEnabled:boolean}` controls only the labelled Development payment panel.

- [ ] Write component tests RED for fresh order list/detail, purchased-price totals (never current catalog price), no shipping/null tracking, owner return Requested and Completed financial reads, payment-key retry/recovery after navigation, return reason stable retry, forbidden/not-found and nonleaking account switch.
- [ ] Implement paginated owner orders/detail, cancellation action only for eligible state with server409 explained, payment recovery/list/create using stable keys, current payment/refund reads, timeline without actors, delivered-order return request/read. No returns warehouse/admin simulation or pretend automatic completion.
- [ ] Default checkout creates/replays one Pending payment as in Task9 and the UI explicitly says Pending/no real provider; GET /ui/config does not silently pay. Opening an existing order reads its payment list without automatically creating another attempt. Development labels local simulation and calls only existing owner /dev/payment routes for explicit success/failure/timeout, then refreshes state. Handle asynchronous shipment404 with Retry/read action without creating another payment.
- [ ] Real Development browser flow: owner creates/payment-simulates order; await shipment; fresh operator fixture advances shipped/delivered via existing HTTP/CLI; owner reads timeline and requests return. Verify other user404 and actors absent; refund/return completion remains existing HTTP admin walkthrough with UI refreshing live totals. Restore default environment afterward.
- [ ] Run components/build/real browser lifecycle and existing shipment/return/financial HTTP suites. Record customer UI complete.

### Task 11: Catalog admin React screen

**Create:** frontend/src/features/admin/ProductAdminPage.tsx/ProductForm.tsx/admin.test.tsx, e2e/catalog-admin.spec.ts.
**Modify:** routes/app shell/style/API bindings.

**Interfaces:** Consume Task3 admin DTOs/routes, AuthProvider permissions and safe money parser. Create form name/description/category/price(initial decimal input)/initial stock; edit form same details+expectedVersion without stock input.

- [ ] Write RED component tests: permission-denied route without products:manage; create initial stock; edit omits stock and sends exact loaded version; stale409 preserves user edits and offers explicit reload; SQL save success shown without claiming instant search update. Null/length/unsafe-price errors readable.
- [ ] Implement list/name/category filters/pages, create and detail edit forms, fixed input limits, in-flight/validation/error states. Frontend permission check is UX only; server403 remains authoritative. Warn/reload after conflict rather than blindly overwriting a newer product.
- [ ] Real Playwright grants only fresh registered catalog fixture via operator CLI; old token denied, fresh login exposes admin, create/update flows synchronizing to sorted search; customer/shipment-only/return-only users get403 by direct requests/hash route. Confirm stock reservation made while admin edit open is preserved after save.
- [ ] Run frontend tests/build and catalog/admin SQL/HTTP/browser tests. Document operator login/grant and asynchronous search; record storefront/admin feature milestone.

### Task 12: Full integration, docs and independent review

**Create:** docs/storefront.md; complete docs/observability.md/frontend/README.md and verification scripts.
**Modify:** root README, docs/README.md/api.md/docker.md/architecture.md/workflows.md/verification.md/redis.md, Http/products.http, CI.

- [ ] Update capability/endpoint tables and code map; explain Node/Vite dev proxy versus same-origin Docker, hash URLs, login memory/reload, pending simulated payments, operation-key retry, no automatic cart clear, no stock-edit/delete, permissions/fresh login, sorting cache v3, dashboard overlay/login/internal OTLP/metrics and trace examples. No placeholder promises or fake storefront data.
- [ ] CI: npm ci/typecheck+build/unit, dotnet build/unit, default frontend/API/browser checks, Development lifecycle/admin browser checks and telemetry smoke, restore default, isolated SQL new runners, paused/drained broker/product-sync correlation, restore API, existing rate-limit/recovery checks. Upload failing Playwright screenshots/traces, never tokens/bodies/secrets. Existing CI cleanup on ephemeral stack remains valid; local checks never down -v.
- [ ] Run full `npm --prefix frontend test`, `npm --prefix frontend run build`, `dotnet build Ecommerce.sln --no-restore`, `dotnet test Ecommerce.sln --no-build --no-restore`; --verify, --verify-sqlserver, --verify-catalog, --verify-telemetry, --verify-shipments, --verify-returns. Expected all pass, isolated DB cleanup, existing warnings only/new warnings addressed.
- [ ] Rebuild Docker; run catalog/sorting/admin/support HTTP plus existing search/cart/checkout/health/financial/shipment/returns scripts. Real default browser suite (no simulator); Development browser suite and existing payment/refund simulation. Real optional dashboard cache/dependency/business counters, connected delayed trace and viewer-outage checks. Inspect UI on desktop/mobile and correct broken navigation/layout before claiming completion.
- [ ] Drain live/retry queues, pause API, run --verify-rabbitmq (new origin/producer/consumer correlation) and --verify-product-sync; restore default API in finally. Confirm five healthy services, dashboard overlay removed, no unknown API/static route returns HTML, simulators absent, no browser token persisted. Record retained fixture accounts/products/commerce data and clean git diff --check.
- [ ] Build feature-only review package against exact baseline including earlier untracked contents. One fresh independent reviewer after all milestones, focused on five Review Focus conditions and spec. Fix blocking findings with observed reproducing RED→GREEN tests and full green suites in one pass; ledger deferred minors/rulings. Preserve prior implementation, use only coherent scoped commits, no unrequested push/merge/deploy. Keep ledger/baseline if changes remain uncommitted.

## Self-review / coverage map

Spec public catalog/sorting→Tasks1/2; admin/concurrency→Task3; opaque Identity/config/payment recovery→Task4; safe metrics→Task5; durable traces/viewer→Task6; build/static routing→Task7; discovery/session/money→Task8; cart/retry→Task9; customer lifecycle→Task10; admin UI→Task11; full regression/docs/viewer/browser/privacy→Task12. Each shared interface is defined before use. Review Focus tests are assigned to Tasks8/9/3/5–6/7 respectively. No unresolved product choices or unimplemented type references are delegated to a future spec.

## Execution handoff

Review this plan before product implementation. Native/inline execution is recommended because the frontend consumes the catalog/auth/payment contracts, telemetry extends their shared Outbox envelope, and the final Docker image integrates all milestones. Native means implement here with a baseline/ledger and one fresh final reviewer. Subagent-driven means a fresh implementer/reviewer gate for each task plus final review, at higher context cost. Preserve any execution method the user explicitly chooses; implement all requested milestones without new approval pauses once plan/execution are approved.
