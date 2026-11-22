# Public HTTPS ingress on Linux

A systemd system socket owns one dedicated IPv4 address on TCP 443. It passes
accepted connections to an unprivileged `systemd-socket-proxyd` service, which
forwards TCP to `127.0.0.1:28443`. Kestrel serves TLS from the chiseled edge
container. The proxy neither terminates TLS nor supplies a client IP header.
Keep broker and workers unpublished on their private container network.
[systemd TCP proxy](https://www.freedesktop.org/software/systemd/man/latest/systemd-socket-proxyd.html)

## Prepare identity and edge configuration

Use an established OAuth/OIDC provider operated independently of Hosting.
Provision an OAuth client registration for each intended client, with its
exact redirect URIs and authorization code flow with S256 PKCE. Provision
a resource or API registration that issues signed,
short-lived JWT **access tokens** with a single audience equal to
`https://edge.example.net/mcp`, a stable `sub` and the configured scope in
`scope` or `scp`. Configure the provider's exact HTTPS issuer string, including
any path or trailing slash it advertises. Both discovery and JWKS must be HTTPS,
with trusted certificates and usable RSA or EC signing keys.

Set the same issuer, audience and scope on broker and edge. Apply the approved
human issuer-and-subject identity and its project, lane and action grants in the
policy files used by both services. Use [deployment configuration](../README.md#configuration)
for their other settings. The broker and edge reach the provider by its HTTPS
issuer. Operate the provider separately from the Hosting project and its volumes.

For rootless Podman Quadlet, add the following to the private `edge.env`
consumed by the edge unit. Keep `PublishPort` bound to loopback.

```ini
Mcp__PublicIngress__Enabled=true
Mcp__PublicIngress__Hostname=edge.example.net
Mcp__Authentication__Issuer=https://identity.example.net
Mcp__Authentication__Audience=https://edge.example.net/mcp
Mcp__Authentication__Scope=sessions
```

For Docker Compose, set the authentication values in the private `edge.env`
and layer the public ingress override over the base file. Export the exact
hostname as `HOSTING_PUBLIC_HOSTNAME` in the operator shell or supply it through
Compose's interpolation environment. Keep both file arguments on subsequent
Compose commands for this deployment.

```sh
export HOSTING_PUBLIC_HOSTNAME=edge.example.net
export HOSTING_EDGE_PORT=28443
docker compose -f deploy/compose.yaml -f deploy/public-ingress/compose.yaml config --quiet
docker compose -f deploy/compose.yaml -f deploy/public-ingress/compose.yaml up --build --detach
```

The override sets public readiness mode and the required hostname on the edge.
The base file continues to define loopback publication, read-only mounts,
networks, state and the broker and worker services.

The edge verifies the audience, a human subject with a grant, and issuer
discovery before Kestrel opens its listener. The live readiness probe checks the
serving policy and the authentication handler's bounded discovery cache. A
missing or disabled setting removes the readiness route. Keep the same
loopback-only port in the Quadlet `PublishPort` or Compose port configuration
and in both system units.

## Issue and renew the TLS certificate

Use a maintained ACME client from your distribution or its official release
outside the application image. Use separate ACME staging state and prove the
challenge before requesting a production certificate.

- Use DNS-01 with approval and credentials scoped to the validation TXT record.
  This method allows issuance and renewal while the public listener is inactive.
- Use TLS-ALPN-01 if external TCP 443 reaches the host and the ACME client can
  exclusively bind the dedicated address during a maintenance window. Before
  standalone renewal, stop this deployment's socket and proxy and confirm the
  address is free. The challenge listener terminates when the client exits.
- Use HTTP-01 when approved TCP 80 routing and an isolated challenge listener
  are available. Remove that listener after validation.

Keep account state and private keys in restricted storage outside the checkout,
with no group or world access to the keys. Stage the serving leaf and chain as a
PFX, or configure Kestrel with PEM certificate and key paths within the edge's
private read-only mount. Limit read access to the mapped edge user and
operators. Keep broker mTLS credentials in their own role-specific files.

Renew before the expiry margin in preflight. Verify the renewed chain, exact DNS
SAN and key match. Stage the renewed bundle with the correct ownership, restart
only the edge, and repeat preflight and authenticated acceptance before starting
the socket. During rollback, use the last valid bundle and repeat the same
checks. [Let's Encrypt staging](https://letsencrypt.org/docs/staging-environment/)
[Let's Encrypt challenges](https://letsencrypt.org/docs/challenge-types/)

## Verify and start the dedicated socket

1. Start the edge privately. As the rootless deployment user, inspect actual
   port mappings and confirm the edge has the sole
   `127.0.0.1:28443 -> 8443/tcp` publication. Confirm the broker and worker
   have no published ports.
2. Run preflight on the deployment host. Use the exact hostname, local
   dedicated IPv4, loopback port and issuer. The host needs Bash, curl, dig,
   iproute2, jq and OpenSSL. The check requires one direct A record, no AAAA,
   a single IPv4 loopback edge listener, trusted TLS with the exact DNS SAN
   and at least 24 hours of validity, live OAuth readiness, exact protected
   resource metadata and an anonymous HTTP 401.

   ```sh
   bash deploy/public-ingress/preflight.sh edge.example.net 192.0.2.10 28443 https://identity.example.net
   ```

3. Independently qualify a real OAuth client and allowed user against the
   private listener with the production hostname and SNI. Use a private
   client-side connection override or tunnel that maps the resource's
   hostname and port 443 to the loopback edge port. Verify MCP
   initialization and a granted tool operation using the real access token.
   Verify an ungranted subject is denied. The anonymous 401 and readiness
   response alone do not prove OAuth client acceptance.
4. Edit private copies of the two sample units for the exact address, port,
   hostname and issuer. Verify the amended files with
   `systemd-analyze verify hosting-public-edge.socket hosting-public-edge.service`.
   Install the preflight as a root-owned file at
   `/usr/local/libexec/hosting-public-edge-preflight` and the two units as
   root-owned files in `/etc/systemd/system`. Give only root write access to
   those files and parent directories. Confirm the proxy executable path on
   the host. Reload and start only the named socket. Its pre-start check
   repeats host preflight using host trust and DNS.

   ```sh
   sudo systemctl daemon-reload
   sudo systemctl start hosting-public-edge.socket
   sudo systemctl status hosting-public-edge.socket
   ```

5. From an authorized external client, verify the trusted HTTPS endpoint,
   OAuth discovery, an allowed MCP operation, and anonymous denial. Record
   the results privately. This sample uses a manual socket start so every
   activation follows deployment review.

## Stop or remove the ingress

Stop the socket and proxy together to close new connections and terminate
forwarding. During removal, delete just the two installed ingress units and
their preflight copy, then reload systemd. Verify TCP 443 on the dedicated
address is closed. Manage the private Podman deployment and its volumes via
the [lifecycle runbook](../lifecycle.md).

```sh
sudo systemctl stop hosting-public-edge.socket hosting-public-edge.service
```
