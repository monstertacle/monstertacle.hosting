# Container Deployment Lifecycle

## Operator scenarios

1. **Preflight and install.** An operator renders generated fixtures for Compose and rootless Quadlet before starting services. The check rejects changes to the private configuration mounts, separate state volumes or loopback-only publication.
2. **Replace the MCP edge.** An operator replaces the edge after changing its serving certificate. The edge has a new container ID. The running broker and worker retain their IDs, start times and mounts. The new TLS identity is served, worker claims still succeed over broker mTLS, and anonymous MCP returns 401.
3. **Recreate the stack.** An operator stops all services, then recreates them with the original volumes. Every container ID changes. Distinct markers in both state volumes survive, and the edge starts without a persistent volume.
4. **Rehearse stopped recovery.** With all fixture writers stopped, an operator archives both complete volumes and restores them to new names. The original and restored contents, including SQLite data and any sidecars present, have matching bytes, modes and numeric ownership before startup. The restored stack starts in isolation and passes the transport and authentication checks.

## Requirements

- Use the existing .NET 11 chiseled runtime targets, unprivileged user, read-only roots, private broker mTLS and separate broker and worker state. Keep the probe and archive tools outside the application images.
- Give fixtures unique resource names, private scratch and a loopback-only edge listener. Scope teardown to resources owned by the rehearsal.
- Provide an operator runbook covering preflight, installation, certificate updates, stopped archive and restore, image and host compatibility, verification before cutover and rollback.
- Hosting owns state schema, worker identities and leases. Live issuer and public ingress acceptance remain separate.

## Acceptance evidence

Compose and rootless Quadlet smoke show exact container identities and volume names across stages. HTTPS discovery, worker mTLS and anonymous denial are checked after recreation and restore. Existing application tests supply authenticated session and recovery evidence.
