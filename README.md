# Monstertacle Hosting

Monstertacle Hosting is a .NET 11 control plane and worker host for durable
sessions accessed over MCP Streamable HTTP. The control plane owns admission
and result custody. Workers run registered execution plans.

## Delivery order

1. [Identity, admission and reconnect](specs/001-sessions/spec.md)
2. [Worker coordination and cancellation](specs/002-coordination/spec.md)
3. [Runtime integration and verified results](specs/003-results/spec.md)
4. [Packaging and qualification](specs/004-deployment/spec.md)

The repository uses .NET 11 (`net11.0` and `net11.0-windows` where applicable),
C#, and shell or PowerShell. See the [constitution](.specify/memory/constitution.md).

## Build and test

Install the .NET 11 SDK specified in `global.json`. From a fresh checkout run:

```sh
git submodule update --init --recursive
dotnet restore
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
```

The initial solution includes the three host entry points and version 1
admission contract. The current contract tests exercise input limits and repeatable
payload digests. Service admission and reconnect are tracked in the feature plans.

## Private broker API

The control plane uses SQLite on a persistent local volume. Set
`Broker__DatabaseFile` to a writable database path with an existing parent
directory. Configure a Kestrel HTTPS endpoint and server certificate. The
private endpoint requires the edge client's TLS certificate, pinned by
`Broker__Authentication__EdgeCertificateSha256`, and a bearer access token.
Set `Broker__Authentication__Issuer`, `Broker__Authentication__Audience`, and
`Broker__Authentication__Scope`. The issuer serves HTTPS OpenID Connect
metadata and signing keys. The discovery issuer and signed token issuer must
match the configured issuer exactly. The broker trusts supported asymmetric
signing keys from the HTTPS JWKS URL supplied by that metadata. It refreshes
that trust within five minutes and rejects invalid discovery on refresh. The
API requires a single exact audience.

Set `Broker__PolicyFile` to an operator-managed JSON file. Replace the file
atomically when changing grants. Each request reads and validates the current
file. This example grants two identities access to the same project, with
independent session ownership.

```json
{
  "version": 1,
  "projects": [{ "id": "demo", "lanes": ["primary"] }],
  "people": [
    {
      "id": "first",
      "identities": [{ "issuer": "https://identity.example", "subject": "first-subject" }],
      "grants": [{ "project": "demo", "lane": "primary", "actions": ["start", "read"] }]
    },
    {
      "id": "second",
      "identities": [{ "issuer": "https://identity.example", "subject": "second-subject" }],
      "grants": [{ "project": "demo", "lane": "primary", "actions": ["start", "read"] }]
    }
  ]
}
```

- `POST /v1/sessions` accepts `mutationId`, `project`, and `task`. It returns
  201 for admission, 200 for an authorized identical retry, and 409 for
  authorized UUID reuse with a changed task.
- `GET /v1/sessions/{sessionId}` reads the current session and child identities.
- `GET /v1/sessions/by-mutation/{project}/{mutationId}` recovers a lost response.
- `GET /v1/sessions?project=demo&limit=20` lists authorized sessions. Pass the
  returned `nextAfter` as `after` to continue.

The broker initially persists jobs in the queued state. Runtime dispatch
remains tracked in the feature plans.

### Worker claims and cancellation

Register each worker by its stable ID with `Broker__Workers__<id>__Lane` and
`Broker__Workers__<id>__CertificateSha256`. Each worker presents its own
certificate on the private HTTPS endpoint. The broker checks the worker
and lane against each claim. Keep the worker ID and its durable local state
together across restarts.

- `POST /v1/worker/claims` accepts a persisted `mutationId` and returns a
  lane-bound job and fence, or 204 when no eligible job is queued. Retrying
  a successful claim returns that same job and fence.
- `POST /v1/worker/jobs/{jobId}/claims/{fence}/dispatch` authorizes dispatch
  and returns the task after checking current admission grants. Repeating the
  call retains the same job and fence. The worker reconciles runtime execution
  against that durable identity.
- `GET /v1/worker/jobs/{jobId}/claims/{fence}` reads the claim and cancellation
  intent for that worker.
- `POST /v1/sessions/{sessionId}/cancel` requires `read` and `cancel` grants
  for every child lane and takes a `mutationId`. Cancellation closes pending
  jobs immediately. When dispatch authorization wins first, the job retains
  its state with cancellation intent until runtime evidence arrives.

Each project admits up to 256 sessions with pending or authorized work.

## MCP Streamable HTTP

The MCP host consumes the .NET 11 target of the MIT-licensed Communication
adapter at the public revision pinned in `dependencies/communication`. Its
license is in the submodule. CI initializes the pinned source recursively.

Serve the edge on HTTPS. Configure `Mcp__Authentication__Issuer`,
`Mcp__Authentication__Audience`, and `Mcp__Authentication__Scope` to match the
broker. The audience is the public HTTPS URL ending in `/mcp`. Clients discover
the authorization server at `/.well-known/oauth-protected-resource/mcp`.
Configure `Mcp__PolicyFile`, `Mcp__BrokerUrl` (a private HTTPS origin),
`Mcp__BrokerClientCertificateFile`, and, when needed,
`Mcp__BrokerClientCertificatePassword`. Provision trust for the broker server
certificate on the edge. Configure the broker to pin the edge client certificate.

Use `session_start` with a stable mutation ID. A new client can call
`session_lookup` with the project and mutation ID, or `session_status` with
the returned session ID. SDK-based tests exercise the actual HTTPS edge and
broker using generated certificates and a deterministic identity fixture.
