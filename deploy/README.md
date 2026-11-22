# Deploy Monstertacle Hosting

The Linux image has three build targets for .NET 11. Each uses the Ubuntu
26.04 chiseled extra ASP.NET Core runtime, which includes ICU, time-zone
data and system CA roots. The broker and worker
have no published ports. The MCP edge publishes HTTPS on loopback in the
examples. The broker and edge join an outbound network for issuer discovery
and JWKS. Publish the edge through a deployment-approved HTTPS ingress.
Keep broker traffic on private networks and preserve client certificate
authentication on the broker connection.

Keep ASP.NET Core 11 Kestrel as the TLS application server. Within Hosting,
only the public MCP edge needs a publicly trusted certificate, such as one
from Let's Encrypt. Run ACME issuance and renewal outside the application
image and mount the renewed certificate directory read-only. Kestrel's
`Certificate` settings accept a PFX via `Path` or a PEM chain and key via
`Path` and `KeyPath`. Keep broker and worker mTLS certificates separate.
Public MCP ingress needs approved TCP 443 routing to the edge. DNS-01
requires control of the validation DNS record and avoids opening TCP 80.
HTTP-01 requires public TCP 80 for the challenge.
[Kestrel endpoints](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints)
and [Let's Encrypt challenge types](https://letsencrypt.org/docs/challenge-types/).

See the [public ingress runbook](public-ingress/README.md) for the dedicated
IPv4 TCP socket and OAuth activation checks.

See the [lifecycle runbook](lifecycle.md) for certificate replacement,
coordinated stopped backup and restore, cutover and rollback.

## Configuration

Start from a recursive checkout so the pinned Communication source and license
are available. Create a private configuration directory outside the checkout.
Each mounted directory must be readable by its own non-root container user.
Keep file permissions and host access restricted to the corresponding service
and operators. Provision the following files:

| Service | Files in its subdirectory | Settings in its environment file |
| --- | --- | --- |
| Broker | `broker/server.pfx`, `broker/policy.json` | `Broker__Authentication__Issuer`, `Broker__Authentication__Audience`, `Broker__Authentication__Scope`, `Broker__Authentication__EdgeCertificateSha256`, `Broker__Workers__<id>__Lane`, `Broker__Workers__<id>__CertificateSha256`, `Kestrel__Endpoints__Broker__Certificate__Password` when needed |
| Edge | `edge/server.pfx`, `edge/client.pfx`, `edge/policy.json` | `Mcp__Authentication__Issuer`, `Mcp__Authentication__Audience`, `Mcp__Authentication__Scope`, `Mcp__BrokerClientCertificatePassword` and `Kestrel__Endpoints__Edge__Certificate__Password` when needed |
| Worker | `worker/client.pfx` | `Worker__Profile`, `Worker__TrustScope`, `Worker__BrokerClientCertificatePassword` when needed |

Keep the issuer, audience and required scope the same at the broker and edge.
The configured audience is the actual public HTTPS resource URI including
`/mcp`. Issue the broker's server certificate for the private DNS name
`broker`. If it uses a private CA, mount a CA bundle that retains the
necessary system trust roots and set `SSL_CERT_FILE` in the edge and worker
environment files. Mount the policy directory, rather than an individual
policy file, so atomic replacement makes revocations visible to running
services. Keep broker and worker state in separate persistent volumes and
back them up consistently. Give every worker its own stable identity,
certificate, trust scope and volume.

The supplied image runs a credential-free deterministic worker. For actual
agents, build a deployment composition using the public AgentFramework
registration API, select the profile server-side, and supply the provider
credentials and required outbound access at deployment.

## Docker Compose

Create `broker.env`, `worker.env`, and `edge.env` alongside the subdirectories
above. Set `HOSTING_CONFIG_DIR` to that absolute directory. From the repository
root, run:

```sh
docker compose -f deploy/compose.yaml config --quiet
docker compose -f deploy/compose.yaml up --build --detach
```

The edge listens on `127.0.0.1:8443` by default. Set `HOSTING_EDGE_PORT` for
another local port. Use `docker compose -f deploy/compose.yaml down` to stop
the deployment while retaining state volumes.
For public deployment, layer the [ingress override](public-ingress/README.md#prepare-identity-and-edge-configuration)
over the base file and qualify the edge on loopback before starting the system socket.

## Rootless Podman Quadlet

Build the same targets from the repository root:

```sh
podman build --target control-plane -t localhost/monstertacle-hosting-control-plane:local .
podman build --target worker-host -t localhost/monstertacle-hosting-worker-host:local .
podman build --target mcp-host -t localhost/monstertacle-hosting-mcp-host:local .
```

Use the private configuration layout above at
`~/.config/monstertacle-hosting`. Place the files from `deploy/quadlet` under
`~/.config/containers/systemd`. Reload the user manager with
`systemctl --user daemon-reload` and start
`hosting-broker.service hosting-worker.service hosting-edge.service` with
`systemctl --user start`. The examples use local images and Linux volume
ownership mapping. Install them on a host with working rootless user namespaces.

## Offline checks

From a clean recursive checkout, run `dotnet build -c Release` and
`dotnet test --no-build -c Release`. Hosted CI runs these on Linux and native
Windows. The Windows service executables use .NET 11, with WorkerHost using
the Windows-specific native lease package and a private local directory.
The image, Compose and Quadlet examples target Linux.

`bash deploy/smoke.sh --config-only` renders and checks private Compose fixtures.
Add `--quadlet` to render Quadlet fixtures instead. Without `--config-only`,
the smoke builds the three chiseled images, verifies the initial stack, replaces
only the edge and its TLS certificate, then recreates the stack with the original
volumes. It archives the stopped broker and worker volumes, verifies the restored
contents in fresh volumes and boots the restored stack.

Both variants verify private mTLS, edge HTTPS and OAuth resource metadata, anonymous
MCP denial, and runtime security and state mounts. The application tests separately
exercise authenticated sessions, recovery and artifact handling. The Quadlet run
requires rootless Podman and an active user systemd manager. Set `HOSTING_EDGE_PORT`
to a free loopback port when running concurrently with other workloads.

## Debugging chiseled containers

The runtime images have no shell or package manager. From the host, use
`docker logs` or `podman logs` for startup errors, and engine `inspect` to
check the configured user, mounts and networks. Use engine `cp` to retrieve
specific files for local inspection. For a consistent SQLite backup, use the
deployment's state backup procedure.

## Live acceptance

Provide an approved disposable public HTTPS endpoint, OAuth issuer and
registrations, two operator-provisioned allowed accounts, one unlisted account
that can obtain a token for this resource, and an authorized real agent
configuration. Use a web MCP client with write tools enabled, a second web
MCP client, and a CLI client that supports Streamable HTTP and the configured
OAuth flow.

1. Verify discovery and sign-in from each client. Confirm that wrong-resource
   tokens and the valid unlisted account cannot discover or invoke tools.
   Start from each allowed account and verify cross-account isolation.
2. Start a bounded two-lane task from one web client and reconnect from a
   fresh session in the second. Repeat with the client roles reversed.
   Drop a start response and retry the same mutation ID after restarting the
   edge. Confirm one session, the same child job IDs and one dispatch per lane.
3. From the CLI, retrieve an artifact and verify its byte count and SHA-256
   against the manifest. Explicitly accept the complete aggregate digest.
   Remove a grant and retry with the existing unexpired token, cached mutation
   IDs and artifact reference. Verify denial and continued access for the
   remaining allowed principal.
4. Exercise cancellation before dispatch and during a real provider call.
   Record whether active stop is confirmed, unsupported or uncertain. Test
   partial results after a child failure or interruption.

Record platform, exact revision, client types, scenario outcome and deployment
prerequisites separately from offline evidence. Live validation requires the
deployment inputs above.
