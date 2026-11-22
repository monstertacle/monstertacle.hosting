# Implementation tasks

- [x] Attempt controlled ACME staging and, if staging succeeds, production issuance. Record bounded results privately.
- [x] Add fail-closed public edge readiness for exact resource, human grants and issuer signing keys.
- [x] Implement host preflight and add dedicated socket and unprivileged TCP proxy examples.
- [x] Document private certificate custody, renewal, preflight, activation and scoped rollback.
- [x] Verify systemd units and qualify the isolated rootless deployment with trusted Kestrel TLS, authenticated MCP, anonymous denial and negative readiness cases.
- [x] Verify the Compose override changes only the edge readiness settings and requires a hostname.
- [x] Verify all four CI jobs on the exact pull request head `cf7ebf4` and main merge commit `b071444`, then record the deployment evidence privately. [Pull request CI](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45021219240) · [Main CI](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45021381750) · [Hosted OIDC loopback smoke on main](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45021395773)
- [ ] Provision an independently operated production OIDC issuer, OAuth client and API registrations, and an explicit human allowlist with grants. Verify live browser and CLI access before activating public ingress.
