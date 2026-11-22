# Tasks: Single-host deployment release bundle

- [x] T001 Run the repo Spec Kit scripts and define feature scope and acceptance.
- [x] T002 [US1] Package build-free templates and checksum-verifiable image assets in `deploy/release/`.
- [x] T003 [US2] Add shared private bootstrap, two-worker fixture and executable MCP acceptance in `deploy/release/`.
- [x] T004 [US3] Add both engine checks to `.github/workflows/ci.yml` and guarded publication in `.github/workflows/deployment-release.yml`.
- [x] T005 [US1] Document download, private configuration and cleanup in `deploy/release/README.md`.
- [x] T006 Run capped local checks, push the topic, and merge after green checks at the exact PR head.
- [x] T007 Verify main, publish the deployment release and prove anonymous asset download plus source-free execution.
- [x] T008 Record measured local, hosted and public release evidence.

## Next deployment stage · Separate authorization

- [ ] Production enrollment and loopback qualification. Requires the approved
  external OIDC issuer and grants, deployment-managed TLS and broker mTLS,
  and deployment-owned worker composition. Accept when the intended user
  can submit a session and retrieve results from both worker lanes through
  the local edge, and unauthorized callers are denied.
- [ ] Live public acceptance. Requires accepted loopback qualification,
  approved DNS and ingress routing, a valid ACME certificate, and explicit
  authorization to activate the endpoint. From an external MCP client,
  verify trusted HTTPS, issuer discovery, unauthorized denial, and a
  granted session with result retrieval.
