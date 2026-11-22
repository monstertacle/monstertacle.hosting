# Local MCP pilot plan

Document the human path from the host with Codex CLI and the current Hosting
contracts. Link the separate published .NET tool used by CI workloads.

Reuse the deployment images and smoke-test certificate pattern. Run an HTTPS
fixture issuer on a private rootless Podman network. Give each worker its own
pinned client certificate, trust scope and state. Publish only the edge on
loopback. Use a separate Codex config and a short-lived bearer fixture to
verify transport without accepting a model or OAuth dependency for local
staging. Use deterministic MCP calls to verify structured responses, denials
and persisted custody. Keep test credentials and logs private.

## Constitution check

All Hosting builds use .NET 11. This slice uses the existing authentication,
authorization, recovery and artifact contracts. The fixture issues only
disposable test identities. Public CI and deployment acceptance are tracked
separately.
