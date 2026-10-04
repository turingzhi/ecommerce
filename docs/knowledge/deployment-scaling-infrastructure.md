# Deployment, scaling, and infrastructure

[Learning index](README.md) · [Documentation index](../README.md)

Start with packaging and CI/CD, follow traffic from DNS to the application, then explore scaling, orchestration, storage, and safe rollouts. Code snippets are general examples unless they link to a repository file.

> **In this project:** The [Dockerfile](../../Dockerfile) builds the API image; [Compose](../../compose.yaml) runs the local API and three dependencies. [GitHub Actions](../../.github/workflows/ecommerce.yml) performs CI checks, but there is no registry publish, CD deployment, Kubernetes cluster, load balancer, object storage, or production TLS configuration. The workers currently assume one API instance.

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

An immutable application artifact.

### Container

A running instance of an image.

### Multi-stage Build

Reduces final image size and keeps build tools out of production images.

Core principle:

```text
Build once
Deploy the same artifact everywhere
```

## CI/CD

Typical pipeline:

```text
Git Push
↓
Build
↓
Test
↓
Docker Image
↓
Registry
↓
Deploy
```

CI commonly includes:

- Build
- Automated tests
- Static analysis

CD commonly includes:

- Deployment
- Rollout
- Rollback

## DNS

DNS resolves:

```text
Domain Name
→ IP Address
```

Common record types:

- A
- AAAA
- CNAME
- MX
- TXT

Important concept:

**DNS TTL**

## TLS and HTTPS

TLS provides:

- Confidentiality
- Integrity
- Server authentication

Simplified flow:

```text
TLS Handshake
↓
Certificate Validation
↓
Session Key
↓
Encrypted Traffic
```

A common architecture uses:

**TLS Termination**

at:

- Load Balancer
- Reverse Proxy
- CDN
- API Gateway

## HTTP/2 and HTTP/3

### HTTP/2

Key feature:

**Multiplexing**

Multiple HTTP streams can share one TCP connection.

### HTTP/3

Runs over:

**QUIC / UDP**

One advantage is reducing TCP-style Head-of-Line Blocking between independent streams.

## Reverse Proxy, Load Balancer, and API Gateway

### Reverse Proxy

```text
Client
↓
Reverse Proxy
↓
Backend
```

It forwards requests and hides backend topology.

### Load Balancer

Chooses one backend instance for each request.

Common strategies:

- Round Robin
- Least Connections
- Weighted Round Robin
- IP Hash

### API Gateway

A unified API entry point.

Possible responsibilities:

- Routing
- Authentication
- Rate limiting
- Request/response transformation
- Logging
- API policies
- Versioning

Avoid putting heavy business logic in the gateway.

## Scaling

### Vertical Scaling

Increase resources on one machine:

- CPU
- RAM
- Disk

### Horizontal Scaling

Add more instances:

```text
API A
API B
API C
```

### Stateless API

Application instances should avoid owning permanent request/session state locally.

Shared state should usually live in:

- Database
- Redis
- Object Storage

## Rate Limiting

Common algorithms:

- Fixed Window
- Sliding Window
- Token Bucket
- Leaky Bucket

Uses:

- Prevent abuse
- Protect downstream systems
- Control resource usage
- Reduce brute-force attacks

## Kubernetes Fundamentals

Important concepts:

- Pod
- Deployment
- Service
- Ingress
- ConfigMap
- Secret
- HPA

### Deployment

Declares:

```text
I want N replicas
```

Kubernetes tries to maintain that desired state.

### Service

Provides stable networking to a set of Pods.

### HPA

Horizontal Pod Autoscaler can scale based on:

- CPU
- Memory
- Custom metrics

## Kubernetes Ingress and Service Mesh

### Ingress

Common responsibilities:

- Host routing
- Path routing
- TLS
- Reverse proxying
- Load balancing

### Service Mesh

Focused on service-to-service traffic:

- mTLS
- Retry
- Circuit Breaker
- Tracing
- Traffic control

Common terms:

- **North-South Traffic** — Client ↔ System
- **East-West Traffic** — Service ↔ Service

## Object Storage

Common services:

- Amazon S3
- Azure Blob Storage
- Google Cloud Storage
- MinIO

Do not assume large files should live on local API server disks.

Typical architecture:

```text
Database
→ Metadata

Object Storage
→ Binary data
```

### Pre-signed URL / SAS

Direct client upload:

```text
Client
↓
Ask API for permission
↓
Receive signed URL
↓
Client uploads directly to Object Storage
```

This offloads file traffic from the API server.

### Multipart Upload

Split large files into parts.

### Resumable Upload

Continue after interruption instead of restarting from zero.

### File Security

Important controls:

- File size limits
- MIME validation
- Magic byte / signature validation
- Malware scanning
- Quarantine
- Server-generated storage keys

## CDN

A CDN caches content near users at edge locations.

Important concepts:

- Edge
- Origin
- Cache Hit
- Cache Miss

Good use cases:

- Images
- Video
- JavaScript / CSS
- Downloads
- Some API responses

CDNs may also provide:

- TLS
- DDoS protection
- WAF
- Rate limiting

## Feature Flags

Feature Flags separate:

```text
Code Deployment
≠
Feature Release
```

Uses:

- Gradual rollout
- Kill switch
- A/B testing
- Internal preview

Remove old flags after rollout.

Otherwise they become:

**Stale Feature Flags**

## Zero-Downtime Deployment

### Rolling Deployment

Replace old instances gradually.

### Blue-Green Deployment

```text
Blue = current version
Green = new version
```

Validate Green, then switch traffic.

### Canary Deployment

Example:

```text
5%
20%
50%
100%
```

Gradually increase traffic to the new version.

### Graceful Shutdown

```text
Stop receiving new traffic
↓
Finish in-flight work
↓
Close resources
↓
Exit
```

## Project examples

[Program.cs](../../Program.cs) reads configuration and starts SQL Server migrations. [compose.yaml](../../compose.yaml) supplies local connection settings through environment variables; the password belongs in an ignored `.env`, not source control. [Dockerfile](../../Dockerfile) builds the API image in one stage and runs it with the ASP.NET runtime image in another. Compose starts the API, SQL Server, RabbitMQ, and Elasticsearch together. It is a local stack, not a production deployment.

The GitHub Actions workflow is **CI**: it restores, builds, starts services, checks health/search, runs the custom scenarios and HTTP script, then removes the test stack. It does **not** publish an image to a registry or deploy the app, so this project does not have CD yet.

---

Previous: [Reliability, observability, and testing](reliability-observability-testing.md) · [Learning index](README.md)
