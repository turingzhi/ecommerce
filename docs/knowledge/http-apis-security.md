> Historical note: shipment and return features have been removed. Examples below
> that refer to them describe earlier implementation, not current application behavior.

# HTTP, ASP.NET Core, and security

[Learning index](README.md) · [Documentation index](../README.md)

Learn how an HTTP request reaches application code, how the app identifies the caller, and how it decides what that caller may do. Snippets are teaching examples unless linked to source code.

**In this project:** ASP.NET Core controllers serve a React and TypeScript storefront. ASP.NET Core Identity handles accounts and bearer tokens. Customer routes check ownership; admin routes check separate permission claims. The app limits catalog reads and selected customer writes, including checkout and returns; see [rate policies](../rate-limiting.md). See [Program.cs](../../src/Ecommerce.Api/Program.cs) and the [API reference](../api.md).

## On this page

- [HTTP and Web Fundamentals](#http-and-web-fundamentals)
- [ASP.NET Core Layering](#aspnet-core-layering)
- [Configuration](#configuration)
- [Authentication and Authorization](#authentication-and-authorization)
- [Authorization Models](#authorization-models)
- [Web Security](#web-security)
- [Password Security](#password-security)
- [Secrets Management](#secrets-management)
- [Project examples](#project-examples)

## HTTP and Web Fundamentals

### HTTP

HTTP lets a client send a request and receive a response. A request has a method, path, headers, and sometimes a body. The response has a status code, headers, and sometimes a body.

```http
GET /products/1
POST /orders
Content-Type: application/json
Idempotency-Key: checkout-123
```

These lines show separate requests and example headers. A real `POST /orders` also needs authentication and a JSON body; see the [API reference](../api.md).

| Status | Meaning |
| --- | --- |
| `200 OK` | The request succeeded |
| `201 Created` | A resource was created |
| `204 No Content` | Success with no response body |
| `400 Bad Request` | The request is invalid |
| `401 Unauthorized` | Authentication is missing or invalid |
| `403 Forbidden` | The caller lacks permission |
| `404 Not Found` | No visible matching resource was found |
| `409 Conflict` | The request conflicts with the current state |
| `429 Too Many Requests` | A rate limit was exceeded |
| `500 Internal Server Error` | An unexpected server failure occurred |
| `503 Service Unavailable` | The service is temporarily unavailable |

### REST API

REST-style APIs describe resources with URLs and use HTTP methods consistently:

```text
GET    /users/123    Read a user
POST   /users        Create a user
PUT    /users/123    Replace a user's representation
DELETE /users/123    Delete a user
```

This is a general example. `GET` should not change business state. `PUT` and `DELETE` are intended to be idempotent: repeated requests have the same intended effect, even if their response codes differ. `POST` needs an explicit replay mechanism when duplicates would be harmful. A stateless request carries the information needed to process it, such as its access token.

### Middleware Pipeline

Middleware runs around the endpoint handler:

```text
Request → routing → authentication → authorization → rate limiter → handler
Response ← earlier middleware receives the result
```

Middleware can log, trace, handle errors, add CORS headers, or stop a request early. Order matters: authorization needs the authenticated caller, and a customer-specific limiter needs that identity too. This project sets that order explicitly in [Program.cs](../../src/Ecommerce.Api/Program.cs). ASP.NET Core can also add some middleware automatically for Minimal APIs; see [middleware ordering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware?view=aspnetcore-10.0).

## ASP.NET Core Layering

A common structure is:

```text
HTTP handler → service → DbContext or repository → database
```

### Controller

A controller receives requests, validates their shape, calls a service, and returns a response. Keep it small so business rules can be used and tested outside HTTP. This project groups controllers by feature and keeps customer and admin actions separate. `[HttpGet]` and `[HttpPost]` declare routes; `[Authorize]` protects a controller or action.

### Service

A service enforces business rules, coordinates a workflow, and chooses transaction boundaries. For example, [OrderService](../../src/Ecommerce.Api/Features/Orders/Services/OrderService.cs) checks an idempotency key, reserves stock, and saves the order and event together.

### Repository

A repository hides persistence behind an interface. It can help when it provides a useful application-specific contract. It can also add boilerplate because EF Core's `DbContext` already tracks changes and groups a save. This project uses `ShopDbContext` directly; it has no separate repository layer.

### Dependency Injection

Dependency injection supplies an object with the collaborators it needs. ASP.NET Core's container supports transient, scoped, and singleton lifetimes. `ShopDbContext` and application services are scoped here; the Elasticsearch and Redis clients are singletons. A singleton must be safe to share. See the [C# chapter](csharp-fundamentals.md#interfaces-and-dependency-injection) for examples and worker scopes.

## Configuration

Keep environment-specific settings outside the build. The same artifact can then run in Development, Staging, or Production with different values.

ASP.NET Core reads settings from sources such as `appsettings.json`, environment variables, command-line arguments, and secret stores. Later sources can override earlier ones. For example, Compose uses `Redis__ConnectionString` to set `Redis:ConnectionString`; the double underscore represents a nested key.

Use configuration for a database address or timeout. Keep passwords and other secrets in an appropriate private source. See [Docker setup](../docker.md).

## Authentication and Authorization

### Authentication

Authentication establishes who the caller is. A login exchanges credentials for an access token. The server validates that token on protected requests.

### Authorization

Authorization checks whether that caller may perform this operation. A valid token alone does not grant access to every order or admin action.

### JWT

A JWT is a token format. Its claims may include subject (`sub`), expiry (`exp`), issuer (`iss`), audience (`aud`), roles, or scopes. A signed JWT protects against tampering; its payload is readable unless it is separately encrypted. Servers must validate the signature and expected claims.

This project's Identity bearer tokens use ASP.NET Core's protected token format. They are not JWTs; see the [official Identity token documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0#use-token-based-authentication).

### Access Token and Refresh Token

An access token authorizes API requests for a limited time. A refresh token can obtain a new access token under the issuer's rules. Both are secrets. A browser must store and handle them carefully, and clients must handle expiration.

### OAuth 2.0

OAuth 2.0 defines delegated authorization: a client gets limited access to a resource without receiving the user's password for that resource.

### OpenID Connect

OpenID Connect adds identity information and login behavior to OAuth 2.0. Neither OAuth federation nor OpenID Connect login is configured in this project.

### Machine-to-Machine Authentication

Services can authenticate with client credentials, managed identities, or workload identities. Managed and workload identities can reduce the need to store long-lived credentials. These are options for a future deployment, not this app's current login flow.

## Authorization Models

| Model | Example rule |
| --- | --- |
| Role-based | Only members of the support role may enter a support area |
| Policy-based | Require authentication and a particular permission claim |
| Resource-based | Only the owner may read this order |
| Scope/claim-based | The token must allow the requested operation |

This project uses ownership checks and five independent `permission` claims:

| Claim | Allows |
| --- | --- |
| `products:manage` | Catalog administration |
| `shipments:manage` | Shipment administration |
| `returns:manage` | Return administration |
| `orders:read` | Admin order reads |
| `payments:read` | Admin financial reads |

Possessing one claim does not grant the others. [Program.cs](../../src/Ecommerce.Api/Program.cs) defines the policies; [AdminPermissionGrant](../../src/Ecommerce.Api/Common/Security/AdminPermissionGrant.cs) grants claims to registered accounts through operator commands. Log in again after a grant to receive updated token claims. The [admin guide](../admin.md) explains setup.

## Web Security

### CORS

CORS tells a browser which other origins may read a response. It does not authenticate a caller or stop non-browser clients from sending requests. The built storefront is served by the API from the same origin.

### CSRF

CSRF tricks a browser into sending an unwanted request with credentials it attaches automatically, usually cookies. Defenses include anti-CSRF tokens, suitable `SameSite` cookie settings, and origin checks. Evaluate the actual credential flow; a bearer header added by application code behaves differently from an automatically sent cookie.

### XSS

XSS runs attacker-controlled JavaScript in a user's browser. Render text safely, encode output for its context, avoid untrusted HTML, and use a suitable Content Security Policy as another layer.

`HttpOnly` prevents JavaScript from reading a cookie. Injected scripts may still act as the user, so it does not solve XSS. See [OWASP XSS prevention](https://cheatsheetseries.owasp.org/cheatsheets/Cross_Site_Scripting_Prevention_Cheat_Sheet.html).

### SQL Injection

Do not put untrusted values into SQL text:

```csharp
// Unsafe when input is untrusted:
var sql = "SELECT * FROM Users WHERE Name = '" + input + "'";
```

Pass values as parameters instead. EF Core LINQ does this. The project's `FromSqlInterpolated` calls also parameterize interpolated values while leaving lock hints in the SQL text. This protection does not make arbitrary user-supplied table or column names safe.

### SSRF

Server-side request forgery occurs when an attacker controls an outbound request destination. It can expose localhost, private services, or cloud metadata endpoints. For URL-fetching features, allow known destinations and check DNS resolution, IP ranges, and redirects. URL parsing alone is not enough.

### Path Traversal

Input such as `../../private-file` can escape an intended directory. Choose server-controlled filenames and verify that resolved paths stay inside the allowed directory.

### Command Injection

Do not build shell commands by concatenating user input. Prefer an API or pass validated arguments directly to a process without a shell.

### IDOR / Broken Access Control

Changing a resource ID must not give a customer access to another customer's data. Query with both the resource ID and authenticated customer ID, as [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs) does. Apply this to writes as well as reads.

### Mass Assignment

Accept request DTOs with only permitted fields. Do not bind client input straight into a database entity containing trusted fields such as owner, price, or status. A DTO still needs validation and authorization.

### HTTP contracts and validation

The contract includes fields, status codes, pagination, and errors. Validate request shape in the handler, business rules in the service, and stored rules with database constraints. Checkout takes its customer ID from authentication and its price from SQL Server.

Optional additive fields are usually easier to introduce than renamed or changed required fields. Breaking changes need a rollout or versioning plan. Use stable pagination ordering; offset pages can still shift as rows change. Cursor pagination is an alternative to study, not a current feature.

The [API reference](../api.md) records the actual responses, including differences between endpoint error shapes.

## Password Security

Store passwords using a dedicated password hashing scheme with a salt and suitable work factor. A plain SHA-256 hash is too fast for this purpose. Common schemes include PBKDF2, bcrypt, scrypt, and Argon2.

A salt makes identical passwords produce different stored hashes. The work factor makes each guess more expensive. ASP.NET Core Identity handles password hashing and verification here; application code should use its account APIs.

## Secrets Management

Database passwords, signing keys, API keys, client secrets, and refresh tokens are secrets. Keep them out of Git, logs, and public browser code. Local development can use ignored environment files or user secrets. Deployed systems can use services such as Azure Key Vault, AWS Secrets Manager, or HashiCorp Vault.

Kubernetes Secrets provide a way to distribute secrets, but still need appropriate access controls and encryption configuration. Rotate credentials and plan how running services receive new values. Managed or workload identities can remove some static credentials. This project has local configuration, not a production secret-management service.

## Project examples

Trace `POST /orders` in [OrdersController](../../src/Ecommerce.Api/Features/Orders/Controllers/OrdersController.cs): authentication supplies the customer ID, validation checks the items and key, DI supplies `OrderService`, and the handler maps its result to `201`, `200` for a replay, or `409` for a conflict.

Read an order with `GET /orders/{id}` to see ownership filtering. Compare that with an admin policy to see why customer access and admin permissions are separate. The [rate-limiting guide](../rate-limiting.md) explains fixed windows, per-IP catalog reads, per-customer creation limits, and `429` responses.

For background work, [OutboxWorker](../../src/Ecommerce.Api/Infrastructure/Messaging/Outbox/OutboxWorker.cs) creates a scope before resolving its dispatcher. This gives the batch its own scoped database context. Awaiting database or broker I/O avoids holding a request thread while waiting; it does not make that dependency faster.

---

Previous: [C# fundamentals, encapsulation, and DI](csharp-fundamentals.md) · [Learning index](README.md) · Next: [Data access and concurrency](data-concurrency.md)
