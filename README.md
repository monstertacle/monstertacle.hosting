# Monstertacle Hosting

Monstertacle Hosting is a .NET 11 control plane and worker host for durable
sessions accessed over MCP Streamable HTTP. The control plane owns admission
and result custody. Workers run registered execution plans.

## Delivery order

1. [Identity, admission and reconnect](specs/001-sessions/spec.md)
2. [Worker coordination and cancellation](specs/002-coordination/spec.md)
3. [Runtime integration and verified results](specs/003-results/spec.md)
4. [Packaging and qualification](specs/004-deployment/spec.md)
5. [GitHub Actions workload access](deploy/ci-workload/README.md)

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
For workstation setup and session recovery, see [Use Hosting from Codex CLI](docs/codex-cli.md).

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

### Embedded worker runtime

WorkerHost uses the public Multisession Common, Storage.Sqlite and native lease
packages at version `0.2.0-alpha.10`. Linux targets `net11.0`. Native Windows
targets `net11.0-windows`. To initialize the credential-free runtime, set
`Worker__Runtime=deterministic`, `Worker__StateDirectory` to an absolute path
beneath an existing writable parent directory, and `Worker__TrustScope` to a
stable nonempty UUID. Keep the state directory and trust scope together across
restarts. The runtime creates the directory on its initial start and opens the
existing native-lease-protected store thereafter.


Set `Worker__Profile` to `deterministic-primary` or `deterministic-secondary` for
credential-free execution. Set `Worker__BrokerUrl` to the private HTTPS origin,
`Worker__BrokerClientCertificateFile` to its PKCS#12 client certificate, and
`Worker__BrokerClientCertificatePassword` if needed. The broker must register
the certificate on the worker's lane. The worker must trust the broker's server
certificate.

A worker holds one durable assignment at a time. The broker job ID is the
runtime session ID, and the broker fence is the runtime request ID. On restart
the worker reads its assignment and checks for the original runtime request
before submitting anything. Broker status distinguishes cancellation intent,
unsupported cancellation, and confirmed execution outcomes. An uncertain
execution retains its assignment pending recovery evidence.

#### Deployment-owned agents

For an AgentFramework deployment, reference the WorkerHost project from a
.NET 11 composition root. Create the agent with deployment-injected
credentials and register a factory with a stable agent and adapter identity.
The deployment sets `Worker__Profile` to the registered profile ID.

```csharp
using Monstertacle.Hosting.WorkerHost;
using Monstertacle.Multisession.AgentFramework;

var registration = WorkerRegistration.AgentFramework(
    profileId: "service-profile",
    providerId: "configured-connection",
    modelId: configuredModelId,
    settingsRevision: "1",
    options: new FrameworkAgentOptions { AdapterId = "service-agent-v1" },
    agentFactory: _ => configuredAgent);

await WorkerApplication.RunAsync(builder, [registration]);
```

Here `configuredAgent` is a deployment-owned `AIAgent` created by the same
composition root. Keep its ID and serialization contract stable across worker
restarts. Task input cannot select the agent, profile, model or credentials.
The offline suite executes this registration with a deterministic chat client
through two worker lanes. Live agent qualification requires deployment inputs.

## Verified results

`session_result` returns the current aggregate digest and the status of every
child. A child can have a verified result even while the aggregate is partial.
A complete aggregate may contain failed or cancelled children. The private
equivalent is `GET /v1/sessions/{sessionId}/result`. It requires current
`read` and `result` grants for every child lane.

`session_artifact` takes the session ID, job ID and artifact name in a child
manifest. The private equivalent is
`GET /v1/sessions/{sessionId}/jobs/{jobId}/artifacts/{name}`. Both return a
bounded base64 payload, size and SHA-256 digest. The broker rechecks the
artifact bytes and requires current `read` and `artifact` grants for that
child lane.

Use `session_accept_result` or `POST /v1/sessions/{sessionId}/result/accept`
with the complete aggregate digest and a stable mutation ID to explicitly
accept a result. The broker validates current `read`, `result` and
`accept-result` grants for every lane and verifies stored artifact bytes
before returning a receipt, including on retry. Reading a result does not
create an acceptance receipt.

## Deployment

See the [deployment guide](deploy/README.md) for .NET 11 images, Docker Compose,
rootless Podman Quadlet, and the separate live acceptance procedure.
