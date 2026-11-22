# Tasks

- [x] T001 Add separate workload issuer validation and current policy checks at edge, broker and dispatch.
- [x] T002 Gate all MCP transport methods and add authenticated and anonymous real-route coverage.
- [x] T003 Implement verified GitHub claim matching and a test-only RFC 8693 authority with ephemeral keys.
- [x] T004 Implement submit, status and lookup in the version-pinnable .NET tool.
- [x] T005 Cover negative federation, authorization, durable denial and revocation on the real routes.
- [x] T006 Ship sample operator configuration, a scoped workflow and pack/restore verification.
- [x] T007 Qualify the exact PR and main checks and record hosted OIDC evidence or its external prerequisite.

## Acceptance evidence

- All eight checks passed on PR head `e1838e488758bd4a98e6dba56fcaa3d43d18020e`. [PR #1](https://github.com/monstertacle/monstertacle.hosting/pull/1)
- The feature merge `33a178bdae2cffc91d4406b3d14ddd7c7ed771d4` passed Linux and Windows build, tests and pinned tool pack/restore, plus Docker and rootless Quadlet on Linux. [Main build and test](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45005699171)
- On that same merge revision, one hosted test exchanged a genuine GitHub assertion at a disposable loopback authority and admitted a session through the real HTTPS MCP edge and broker. It rejected a workflow mismatch and verified that an anonymous submission left no durable admission. [Hosted OIDC smoke](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45005700448)

Production use still requires an operator-managed security token service,
a protected GitHub branch and any designated environment, and live qualification
of the deployed MCP endpoint and human OAuth clients. This repository's
`main` was unprotected during the hosted smoke.
