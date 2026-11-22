# Tasks: Single-host deployment release bundle

- [x] T001 Run the repo Spec Kit scripts and define feature scope and acceptance.
- [x] T002 [US1] Package build-free templates and checksum-verifiable image assets in `deploy/release/`.
- [x] T003 [US2] Add shared private bootstrap, two-worker fixture and executable MCP acceptance in `deploy/release/`.
- [x] T004 [US3] Add both engine checks to `.github/workflows/ci.yml` and guarded publication in `.github/workflows/deployment-release.yml`.
- [x] T005 [US1] Document download, private configuration and cleanup in `deploy/release/README.md`.
- [x] T006 Run capped local checks, push the topic, and merge after green checks at the exact PR head.
- [x] T007 Verify main, publish the deployment release and prove anonymous asset download plus source-free execution.
- [x] T008 Record measured evidence. For the next live step, qualify a deployment-owned worker composition and complete loopback preflight with the approved external issuer, grants and ACME material before any separately authorized public activation.
