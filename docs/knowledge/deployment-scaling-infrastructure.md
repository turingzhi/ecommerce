# Deployment, scaling, and infrastructure

[Learning index](README.md) · [Documentation index](../README.md)

Learn how code becomes a running service, how traffic reaches it, and what changes when the service needs more capacity or a safer rollout.

**In this project:** The [Dockerfile](../../Dockerfile) builds the API and React/TypeScript storefront into one image. [Compose](../../compose.yaml) runs that image with SQL Server, RabbitMQ, Elasticsearch, and Redis. [GitHub Actions](../../.github/workflows/ecommerce.yml) runs CI checks. A telemetry viewer is optional. Production hosting, Kubernetes, object storage, a load balancer, and CD are learning topics, not configured infrastructure here.

## On this page

- [Docker](#docker)
- [CI/CD](#cicd)
- [DNS](#dns)
- [TLS and HTTPS](#tls-and-https)
- [HTTP/2 and HTTP/3](#http2-and-http3)
- [Reverse Proxy, Load Balancer, and API Gateway](#reverse-proxy-load-balancer-and-api-gateway)
- [Scaling](#scaling)
- [Rate Limiting](#rate-limiting)
- [Kubernetes Fundamentals](#kubernetes-fundamentals)
- [Kubernetes Ingress and Service Mesh](#kubernetes-ingress-and-service-mesh)
- [Object Storage](#object-storage)
- [CDN](#cdn)
- [Feature Flags](#feature-flags)
- [Zero-Downtime Deployment](#zero-downtime-deployment)
- [Project examples](#project-examples)

## Docker

### Image

An image packages application files and their runtime environment. Build it once and promote the same artifact between environments. An image digest identifies exact content; a tag can point to different images over time.

### Container

A container is a running instance of an image with its own process environment and filesystem view. Persist important data outside its disposable writable layer, such as in a database volume.

### Multi-stage Build

A multi-stage build compiles in build images and copies the output into a smaller runtime image. Build tools then need not remain in the final image.

Here a Node stage builds the storefront, a .NET SDK stage publishes the API, and an ASP.NET runtime stage receives both outputs. The API serves the built browser files. See [Docker setup](../docker.md).

## CI/CD

A common delivery pipeline is:

```text
Push → build → checks → image → registry → deploy → verify rollout
```

Continuous integration (CI) gives feedback on builds, tests, and other checks. Continuous delivery prepares verified artifacts for release; continuous deployment also automates release to an environment. Rollouts need health checks, a rollback path, and compatible database changes.

This repository's workflow builds and tests against a local stack on the CI runner. It does not publish an image to a registry or deploy to a hosting environment.

## DNS

DNS maps names to records used to find services:

| Record | Common purpose |
| --- | --- |
| A | IPv4 address |
| AAAA | IPv6 address |
| CNAME | Alias to another name |
| MX | Mail server routing |
| TXT | Text values such as domain verification |

DNS TTL tells resolvers how long a record may be cached. A DNS change may not reach every client immediately. DNS resolves a destination before the HTTP connection; it is not a request proxy.

## TLS and HTTPS

TLS protects traffic confidentiality and integrity and lets a client authenticate the server through its certificate. In a simplified handshake, peers negotiate settings, validate the certificate, and establish keys for encrypted traffic.

TLS may terminate at a proxy, load balancer, gateway, or CDN. Decide how the next connection to the backend is protected too. HTTPS at the edge does not automatically encrypt every internal hop.

The local Compose API uses loopback HTTP. The stack has no production certificate or TLS-termination configuration.

## HTTP/2 and HTTP/3

### HTTP/2

HTTP/2 multiplexes several streams over one TCP connection. This reduces the need for separate connections, but lost TCP data can still hold up progress on other streams.

### HTTP/3

HTTP/3 uses QUIC over UDP. QUIC handles streams so loss on one does not impose TCP's connection-wide head-of-line blocking on independent streams. It still needs encryption, congestion control, and compatible client/server infrastructure.

These protocols are learning topics, not a deployed edge configuration in this repository.

## Reverse Proxy, Load Balancer, and API Gateway

### Reverse Proxy

A reverse proxy receives requests and forwards them to backend services. It can terminate TLS, route paths, and keep internal service addresses out of the client's view.

```text
Client → proxy → backend
```

### Load Balancer

A load balancer distributes traffic across backend instances. Common strategies include round robin, least connections, weighted routing, and IP hash. Health signals help it avoid instances that should not receive new traffic.

### API Gateway

An API gateway is a shared entry point for routing and API policies. It may apply authentication, rate limits, logging, transformations, or version routing. Keep core business rules in the application so they are enforced wherever the operation runs.

One product can perform several of these roles. None is deployed in the local stack.

## Scaling

### Vertical Scaling

Give one machine more CPU, memory, or storage. This is often simple, but has hardware and cost limits and does not by itself add redundancy.

### Horizontal Scaling

Add instances and distribute work among them. This needs shared state, safe concurrent writes, and coordination for work that must happen once.

Adding API replicas will not fix a database or broker bottleneck automatically. Measure request latency, resource usage, and backlog first.

### Stateless API

Keep durable data and shared session state outside a single API process, using a database, Redis, or object storage as appropriate. Any instance should be able to handle a request with the provided identity and shared state.

This app uses shared SQL and Redis data, but its Outbox publication has no multi-instance row-claiming protocol. Startup migrations and workers also need a deployment plan before using several API replicas. The current local stack runs one API instance.

## Rate Limiting

A rate limiter controls admitted work to reduce abuse and protect capacity:

| Algorithm | Idea |
| --- | --- |
| Fixed window | Allow a set number of requests in each period |
| Sliding window | Measure over a moving recent period |
| Token bucket | Refill tokens over time; allow a bounded burst |
| Leaky bucket | Drain admitted work at a controlled pace |

The project uses fixed windows in [CommerceRateLimiting](../../src/Ecommerce.Api/Common/RateLimiting/CommerceRateLimiting.cs). Catalog reads share a per-IP quota. Order creation, cart checkout, and return requests share one customer quota. Payment creation, customer refunds, and Development simulators share another. Rejections return `429` with `Retry-After`. See [rate limiting](../rate-limiting.md).

Counters are local to each API process. Several replicas would multiply the available quota unless the policy were coordinated. Behind a proxy, define trusted forwarded-header handling before relying on client IP; the current app uses the connection's remote IP.

## Kubernetes Fundamentals

Kubernetes manages containers and their desired running state. The main concepts are:

| Concept | Purpose |
| --- | --- |
| Pod | Smallest deployable group of containers |
| Deployment | Maintains replicas and manages replacement |
| Service | Stable networking to selected Pods |
| Ingress | Rules for external HTTP routing through an ingress controller |
| ConfigMap | Non-secret configuration |
| Secret | Secret distribution, with access and encryption controls still required |
| HPA | Horizontal Pod Autoscaler |

### Deployment

A Deployment declares the desired replicas and container template. Kubernetes works to keep that state as instances stop or versions change. It does not make unsafe application concurrency safe.

### Service

A Service gives clients a stable way to reach a changing set of Pods. It selects matching instances instead of asking callers to track each Pod address.

### HPA

An HPA adjusts replica count using metrics such as CPU, memory, or a custom measure. Choose a signal tied to the bottleneck and set useful bounds. Scaling API replicas cannot add database capacity by itself.

Kubernetes and autoscaling are not installed here.

## Kubernetes Ingress and Service Mesh

### Ingress

An ingress controller implements host/path routing and commonly handles TLS and proxying. Ingress rules alone need a controller to carry them out.

### Service Mesh

A service mesh manages service-to-service traffic. It can provide mutual TLS, traffic policies, retries, circuit breaking, and tracing. These features add configuration and operational cost, and retries still need safe application semantics.

North-south traffic crosses the system boundary, such as client to API. East-west traffic flows between services. This project has neither an ingress controller nor a mesh.

## Object Storage

Object stores such as Amazon S3, Azure Blob Storage, Google Cloud Storage, and MinIO hold binary data under keys. A database can store the file's metadata and ownership while the object store keeps the bytes.

Local API disk can be unsuitable for shared or durable files: another replica may not have the same file, and a replacement container may lose it. Choose storage based on durability, access, and sharing needs.

### Pre-signed URL / SAS

A signed URL grants limited access to one storage operation for a defined time:

```text
Client asks API → API checks permission → API issues signed upload URL
Client uploads directly to object storage
```

This avoids routing all file bytes through the API. Validate the completed upload before treating it as trusted application data.

### Multipart Upload

Multipart upload divides a large file into parts, which can be retried separately before the final object is assembled.

### Resumable Upload

A resumable upload continues from saved progress after interruption. It avoids restarting a large transfer from zero.

### File Security

Check size, allowed types, and file signatures rather than trusting a filename or MIME header. Use server-chosen storage keys, scan where needed, and quarantine files until checks finish. Enforce ownership on upload and download access.

Object storage and file-upload APIs are learning topics; this app does not implement them.

## CDN

A content delivery network caches content at edge locations near users. The origin supplies it on a miss; a hit can avoid contacting the origin.

Images, videos, scripts, styles, and downloads are common uses. Some public API responses can also be cached with a clear key and expiry policy. Personalized or authorized content needs an explicit access/cache policy.

CDNs may also offer TLS, DDoS controls, a WAF, or rate limiting. A CDN is not configured here.

## Feature Flags

A feature flag lets code be deployed before its behavior is enabled. It can support a gradual release, internal preview, experiment, or emergency disable switch.

Define who changes a flag and what happens when its source is unavailable. Remove flags and obsolete paths after a rollout so old combinations do not accumulate.

This app has configuration switches, such as Development-only payment simulation and optional telemetry export. It does not have a general feature-flag service.

## Zero-Downtime Deployment

### Rolling Deployment

Replace instances gradually while others serve traffic. Old and new code must both understand the shared data and contracts during overlap.

### Blue-Green Deployment

Run the current and new versions separately, validate the new environment, then switch traffic. Keep a rollback plan; shared schema changes can still prevent rollback.

### Canary Deployment

Send a small portion of traffic to the new version, observe it, and increase the portion if results are acceptable. Decide the success and rollback signals before starting.

### Graceful Shutdown

```text
Stop accepting new traffic → finish or safely abandon in-flight work
→ release resources → exit
```

Pass stopping tokens to supported operations, set a bounded drain period, and preserve unfinished work for recovery. Idempotency covers cases where a client or worker cannot tell whether the last attempt finished.

These are deployment strategies to study. The repository does not implement a production zero-downtime rollout.

## Project examples

[Program.cs](../../src/Ecommerce.Api/Program.cs) reads configuration, runs migrations, registers workers, and serves static storefront files. [compose.yaml](../../compose.yaml) provides service addresses and local environment settings. Keep local passwords in ignored configuration; follow the [Docker guide](../docker.md) for startup and reset behavior.

The default stack has five services: the API/storefront, SQL Server, RabbitMQ, Elasticsearch, and Redis. The optional [observability overlay](../observability.md) adds a dashboard and OTLP export. It is not a health dependency or a production monitoring service.

The CI workflow builds backend and frontend code, runs unit, SQL, HTTP, browser, broker, and telemetry checks, and cleans up its test stack. See [verification](../verification.md) for commands and scope. This is CI; registry publication and deployed release automation still need to be designed.

---

Previous: [Reliability, observability, and testing](reliability-observability-testing.md) · [Learning index](README.md)
