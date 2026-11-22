# Implementation tasks

- [x] Attempt controlled ACME staging and, if staging succeeds, production issuance. Record bounded results privately.
- [x] Add fail-closed public edge readiness for exact resource, human grants and issuer signing keys.
- [x] Implement host preflight and add dedicated socket and unprivileged TCP proxy examples.
- [x] Document private certificate custody, renewal, preflight, activation and scoped rollback.
- [x] Verify systemd units and qualify the isolated rootless deployment with trusted Kestrel TLS, authenticated MCP, anonymous denial and negative readiness cases.
- [x] Verify the Compose override changes only the edge readiness settings and requires a hostname.
- [ ] Verify exact pull request and main CI, then record private evidence and the remaining live activation prerequisites.
