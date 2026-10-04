# HTTP, ASP.NET Core, and security

[Learning index](README.md) · [Documentation index](../README.md)

Start with the request boundary: HTTP, route handling, configuration, identity, and access rules. Code snippets are general examples unless they link to a repository file.

> **In this project:** This project uses Minimal API endpoints and ASP.NET Core Identity bearer tokens. It checks order ownership in the endpoint and service layers. It does not use controllers, a separate repository layer, OAuth/OIDC federation, or a production API gateway. See [Program.cs](../../Program.cs), [order endpoints](../../Endpoints/OrderEndpoints.cs), and the [API reference](../api.md).

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

HTTP is the application-layer protocol commonly used for communication between clients and servers.

Common requests:

```http
GET /users/123
POST /orders
PUT /users/123
DELETE /orders/123
```

Common status codes:

- `200 OK` — request succeeded
- `201 Created` — resource created successfully
- `204 No Content` — request succeeded with no response body
- `400 Bad Request` — invalid request
- `401 Unauthorized` — not authenticated
- `403 Forbidden` — authenticated but not allowed
- `404 Not Found` — resource not found
- `409 Conflict` — resource conflict
- `429 Too Many Requests` — rate limit exceeded
- `500 Internal Server Error` — server-side failure
- `503 Service Unavailable` — service temporarily unavailable

### REST API

REST-style APIs typically model data as resources.

```text
GET    /users/123
POST   /users
PUT    /users/123
DELETE /users/123
```

Important ideas:

- Resource-oriented design
- Correct HTTP method semantics
- Stateless communication
- Proper use of HTTP status codes

### Middleware Pipeline

A typical ASP.NET Core request flow:

```text
Request
↓
Earlier middleware (for example, exception handling)
↓
Routing: select endpoint
↓
Authentication: identify caller
↓
Authorization: check endpoint access
↓
Endpoint handler (Minimal API or controller)
↓
Response
```

This is a simplified request path. Responses unwind through middleware in reverse order, and middleware can end a request early. Minimal APIs can add routing and authentication/authorization middleware automatically; explicit ordering must still respect their dependencies. See [ASP.NET Core middleware](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/middleware?view=aspnetcore-10.0).

Middleware is commonly used for:

- Logging
- Exception handling
- Authentication
- CORS
- Rate limiting
- Request tracing

## ASP.NET Core Layering

A common application structure:

```text
Controller
↓
Service
↓
Repository / DbContext
↓
Database
```

### Controller

Responsibilities:

- Receive HTTP requests
- Validate input
- Call application/service logic
- Return HTTP responses

Controllers should usually stay thin.

### Service

Responsibilities:

- Business rules
- Workflow coordination
- Transaction boundaries
- Calling repositories and external services

### Repository

A Repository is an abstraction over data access.

Possible benefits:

- Isolates persistence logic
- Easier mocking/testing
- Hides EF Core details

Possible drawbacks:

- `DbContext` already behaves partly like Repository + Unit of Work
- Too much abstraction may create unnecessary boilerplate

Use it when it adds meaningful value.

### Dependency Injection

ASP.NET Core has built-in Dependency Injection.

Common lifetimes:

- `Transient`
- `Scoped`
- `Singleton`

Typical examples:

- `DbContext` → Scoped
- Application service → Scoped / Transient
- Shared thread-safe global service → Singleton

## Configuration

Common sources:

- `appsettings.json`
- Environment Variables
- Secret stores
- Command-line arguments

Common environments:

```text
Development
Staging
Production
```

Core principle:

**Externalized Configuration**

The same build artifact should run in different environments using different configuration.

## Authentication and Authorization

### Authentication

Answers:

> Who are you?

### Authorization

Answers:

> What are you allowed to do?

Important:

```text
Authenticated
≠
Authorized
```

### JWT

A JWT may contain claims such as:

```text
sub
role
scope
exp
iss
aud
```

JWTs are signed, but the payload is not encrypted by default.

### Access Token and Refresh Token

- Access Token — short-lived API access
- Refresh Token — used to obtain new access tokens

### OAuth 2.0

Primarily about delegated authorization.

### OpenID Connect

Built on OAuth 2.0 and adds identity/authentication.

### Machine-to-Machine Authentication

Common approaches:

- Client Credentials
- Managed Identity
- Workload Identity

## Authorization Models

Common models:

- Role-based Authorization
- Policy-based Authorization
- Resource-based Authorization
- Scope / Claim-based Authorization

## Web Security

### CORS

Controls browser cross-origin access.

Important:

> CORS is not Authentication.

### CSRF

Exploits browsers automatically sending cookies.

Mitigation:

- Anti-CSRF tokens
- SameSite cookies
- Origin / Referer checks when appropriate

### XSS

Malicious JavaScript executes in a user's browser.

Mitigation:

- Output encoding
- CSP
- Avoid rendering untrusted HTML

`HttpOnly` prevents JavaScript from reading a cookie, but does not stop injected scripts executing or making requests as the user. It limits one consequence of XSS; context-appropriate output encoding and safe rendering remain necessary. See [OWASP XSS prevention](https://cheatsheetseries.owasp.org/cheatsheets/Cross_Site_Scripting_Prevention_Cheat_Sheet.html).

### SQL Injection

Do not build SQL by concatenating user input.

Bad:

```csharp
$"SELECT ... '{input}'"
```

Use:

**Parameterized Queries**

### SSRF

An attacker controls where the server sends outbound requests.

Possible targets:

- localhost
- internal networks
- cloud metadata endpoints

Mitigation:

- Allowlist destinations
- Block private and link-local ranges
- Safe DNS / URL resolution

### Path Traversal

Prevent input such as:

```text
../../
```

from escaping allowed directories.

### Command Injection

Do not concatenate untrusted data into shell commands.

### IDOR / Broken Access Control

A user must not gain access to another user's resource simply by changing an ID.

### Mass Assignment

Do not bind public API input directly to database entities.

Use DTOs with only permitted fields, then validate their values and check resource ownership. A DTO by itself does not authorize a change.

### HTTP contracts and validation

Treat request fields, response fields, status codes, and error shapes as a contract. Validate at three boundaries: request shape at the endpoint, business rules in the service, and persistent invariants with database constraints. Clients must not choose trusted values such as the authenticated customer ID or checkout price.

For additive changes, introduce optional fields and keep existing consumers working. Renaming required fields or changing their meaning can break clients and needs a migration/versioning plan. Use stable ordering for pagination; offset pages may still shift when new rows arrive. Cursor pagination is an alternative to evaluate for large or rapidly changing lists, not a feature implemented here.

The [API reference](../api.md) records the current contract, including its differing error responses.

## Password Security

Do not store passwords as:

- Plaintext
- Plain SHA256 hashes

Use dedicated password hashing algorithms:

- PBKDF2
- bcrypt
- scrypt
- Argon2

Use:

**Salt**

ASP.NET Core Identity already provides mature password handling.

## Secrets Management

Secrets include:

- Database passwords
- JWT signing keys
- API keys
- OAuth client secrets

Do not commit secrets to Git.

Use systems such as:

- Azure Key Vault
- AWS Secrets Manager
- HashiCorp Vault
- Kubernetes Secrets

Important concept:

**Secret Rotation**

Even better:

**Managed Identity / Workload Identity**

This reduces long-lived static secrets.

## Project examples

| Concept | How to see it here |
| --- | --- |
| HTTP method and status | [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) maps `POST /orders` and `GET /orders`; a new order returns `201`, a replay `200`, and a conflicting key `409`. |
| Authentication versus authorization | [Program.cs](../../Program.cs) enables authentication and authorization; protected routes require a signed-in user. [OrderEndpoints](../../Endpoints/OrderEndpoints.cs) also filters by that user's ID so a valid token cannot read someone else's order. |
| DTO and encapsulation | [CreateOrder](../../Dtos/CreateOrder.cs) is an input shape. The endpoint validates it, the service enforces business rules, and [OrderResponse](../../Dtos/OrderResponse.cs) controls output. A DTO does not replace SQL constraints or transaction checks. |
| Dependency injection | [Program.cs](../../Program.cs) registers scoped `ShopDb` and services, an `IEventPublisher` implementation, and a singleton Elasticsearch client. An endpoint receives `OrderService`; [OutboxDispatcher](../../Services/OutboxDispatcher.cs) receives `IEventPublisher`. |
| Background-service scope | [OutboxWorker](../../Services/OutboxWorker.cs) creates a scope for each batch before resolving scoped services. It does not keep one `DbContext` for its lifetime. |
| `async`/`await` | Endpoints and services await database and broker I/O. Waiting need not occupy a request thread; `await` does not mean “start a new thread” or make SQL itself execute faster. |

ASP.NET Core Identity handles account registration and bearer-token login. Protected endpoints require authentication; order and payment code additionally checks resource ownership. The public API accepts DTOs rather than binding input directly to database entities. SQL values in `FromSqlInterpolated` are parameterized. The current project does not implement OAuth/OIDC federation, roles, an API gateway, or rate limiting. These are separate topics in the general backend guide, not automatic properties of having bearer tokens.

---

Previous: [C# fundamentals, encapsulation, and DI](csharp-fundamentals.md) · [Learning index](README.md) · Next: [Data access and concurrency](data-concurrency.md)
