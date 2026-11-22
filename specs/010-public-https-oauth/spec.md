# Public HTTPS ingress and OAuth readiness

## Operator scenarios

1. The operator prepares one dedicated public IPv4 address and a DNS hostname, obtains a certificate through an approved ACME challenge, and stages the edge on a dedicated rootless loopback port.
2. Before enabling public ingress, the operator verifies DNS, the certificate served by Kestrel, issuer discovery, and an explicitly provisioned human grant. A failed check keeps the public socket inactive.
3. The operator verifies an authenticated MCP request and separately verifies anonymous denial. The broker and workers remain private during installation, rotation and removal.

## Requirements

- Use a systemd IPv4 socket on the dedicated address at TCP 443 and an unprivileged systemd-socket-proxyd service forwarding raw TCP to the fixed loopback high port. Kestrel terminates TLS in the existing .NET 11 chiseled edge.
- Require the expected DNS hostname and IPv4 address, an exact DNS SAN, a valid trust chain and certificate lifetime, and a loopback-only edge publication. The IPv4 reference deployment requires the hostname to have no AAAA records.
- Require TLS-validated OIDC discovery and usable signing keys, the exact provider issuer, and the canonical HTTPS resource URI ending in `/mcp` as the sole access-token audience. Require at least one explicitly configured human subject and grant. Apply those checks to the same configuration used by the serving edge.
- Refuse public activation while any prerequisite is missing, invalid or explicitly disabled. All MCP actions require authentication and authorization. Public OAuth resource metadata supports discovery.
- Keep broker and worker connections private. ACME runs outside the application images with restricted storage. Choose DNS-01 or a tightly scoped TLS-ALPN-01 or HTTP-01 challenge according to site approval and routing. Keep all deployment identities, secrets and evidence outside the public repository.
- Document certificate renewal, the full preflight, narrowly scoped activation, and rollback.

## Acceptance

An isolated rootless Podman deployment must demonstrate an authenticated MCP operation, independent anonymous denial, trusted Kestrel TLS and negative preflight outcomes. Certificate material and fixture tokens stay in private scratch. Exact pull request and main revisions must pass public CI. Live acceptance needs a legitimate provider registration and operator-provisioned subject.
