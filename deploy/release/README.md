# Monstertacle Hosting deployment bundle

The bundle supports Linux x86-64 with Docker Engine 28 or later and Compose
v2 or later, or Podman 5.4 or later with rootless Quadlet, user namespaces and
an active systemd user manager. The quickstart also needs Bash, GNU coreutils,
GNU tar, gzip, curl, jq, OpenSSL 3, and the host CA bundle at
`/etc/ssl/certs/ca-certificates.crt`.

## Download and verify

Choose a `deploy-vX.Y.Z` tag from the
[GitHub releases page](https://github.com/monstertacle/monstertacle.hosting/releases).
Run the following in an empty directory. Replace the example tag when
installing a different version.

```bash
tag=deploy-v0.1.0
name="monstertacle-hosting-$tag-linux-amd64"
base="https://github.com/monstertacle/monstertacle.hosting/releases/download/$tag"
for asset in SHA256SUMS "$name-bundle.tar.gz" "$name-images.tar.gz"; do
    curl --disable --fail --silent --show-error --location --proto '=https' --proto-redir '=https' \
        --output "$asset" "$base/$asset" || exit 1
done
sha256sum --strict --check SHA256SUMS && tar --same-permissions -xzf "$name-bundle.tar.gz"
```

The checksums cover the deployment files and the image archive.
`revision` identifies the source commit and `image-ids.tsv` records the
exact image IDs. The archive contains the three ASP.NET Core 11 chiseled
service images and a Kestrel fixture image. Run one of the following
sequences from the directory where you downloaded the assets.

### Docker Compose

```bash
docker load --input "$name-images.tar.gz" &&
    bash "$name/quickstart.sh" compose
```

### Rootless Podman Quadlet

Run as the user whose systemd manager owns the fixture.

```bash
podman load --input "$name-images.tar.gz" &&
    bash "$name/quickstart.sh" quadlet
```

The demo edge binds `127.0.0.1:38443`. Set `HOSTING_EDGE_PORT` to another
unused unprivileged port if needed. The quickstart generates short-lived TLS,
separate mTLS client identities, a grant for each lane and short-lived
signed JWTs. The fixture issuer serves discovery metadata and public keys
on a private container network with no published port. The quickstart
passes the bearer headers from private files, checks 401 and 403 denials,
starts an authenticated session and retrieves both deterministic artifacts.
It removes the uniquely named fixture containers, networks, state volumes,
Quadlet units and private files when it exits. The loaded release images
remain available for another run.

## Supply your own private configuration

The production templates are `compose.yaml` and `quadlet/`. Use an absolute
private config directory outside Git. Give each role's unprivileged runtime
access only to its own mounted files and apply the following layout.

| Role | Runtime files and environment file |
| --- | --- |
| Broker | `broker/server.pfx`, `broker/policy.json`, `broker.env` |
| Edge | `edge/server.pfx`, `edge/client.pfx`, `edge/policy.json`, `edge.env` |
| Primary worker | `worker/client.pfx`, `worker.env` |
| Secondary worker | `worker-secondary/client.pfx`, `worker-secondary.env` |

Set the same issuer, audience and scope at the broker and edge. Give each
worker a separate broker registration, lane, client certificate and trust
scope. Supply a CA bundle with the broker's CA and the necessary system
roots to clients that need it. The
[deployment configuration guide](https://github.com/monstertacle/monstertacle.hosting/tree/main/deploy#configuration)
lists the environment settings and the contract for deployment-owned
agent composition.

With Compose, export `HOSTING_CONFIG_DIR` and run
`docker compose -f compose.yaml up --detach --no-build --pull never`.
Use `docker compose -f compose.yaml down` to stop the services and keep state.

For Quadlet, place the private inputs at
`~/.config/monstertacle-hosting` or set the paths in the units. Install
`quadlet/` under `~/.config/containers/systemd/`, reload the user manager,
and start `hosting-edge.service`, `hosting-worker.service` and
`hosting-worker-secondary.service`. The units bring up their broker and use
distinct state volumes. The default private qualification port in both
templates is `127.0.0.1:8443`.

The worker image contains the deterministic profiles. Before live use,
qualify the deployment-owned agent composition and complete loopback
preflight with the approved external issuer, grants and ACME material.
The [public ingress runbook](https://github.com/monstertacle/monstertacle.hosting/tree/main/deploy/public-ingress)
covers the separately authorized activation.

## Human access enrollment

Use the [operator enrollment guide](enrollment/README.md) to configure one
shared policy directory and manage human access through the release image.
The disposable release fixture also supports `bash quickstart.sh compose enrollment`
and `bash quickstart.sh quadlet enrollment`.

For a standalone issuer with Docker Compose, see the
[optional Keycloak reference](keycloak/README.md).
Run `bash quickstart.sh compose keycloak` for the disposable real-issuer fixture.
