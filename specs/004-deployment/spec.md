# Feature 004: Packaging and platform qualification

## User scenarios and acceptance

1. A clean checkout restores public dependencies and runs the deterministic
   suite on Linux and native Windows under .NET 11.
2. Container images run the three hosts, with persisted state separate from
   images and only MCP ingress available at the edge. Docker Compose and
   Podman Quadlet provide examples for separate private and edge networks.
3. An operator can follow documented acceptance for three interoperable clients,
   two allowed accounts and a valid unlisted account, restart/retry, revocation,
   multi-child result custody and cancellation. Live checks require approved
   deployment identity, TLS, client access and runtime configuration.
