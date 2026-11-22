# Verification evidence

- Feature creation used the repository Bash `create-new-feature.sh` with 012,
  followed by `setup-plan.sh` and `setup-tasks.sh`.
- Starting main revision was `aea89fae5ff4957f236a7c8db8948ee40ff112`,
  equal to the fetched remote main at creation.
- Implementation topic was refreshed to main revision
  `50cae89` before changes were committed.

## Local qualification · 2026-11-22

- On Linux x86-64 with rootless Podman 5.4.2, the capped
  `build-assets.sh podman` and `verify-assets.sh quadlet` run passed.
  Verification checked both SHA-256 sums, loaded four images, matched their
  IDs, and ran from a new extraction outside Git. Anonymous and
  wrong-audience session starts returned 401. An unlisted subject returned
  403. A granted JWT admitted a session, both lanes succeeded, and the
  verified result and both expected artifacts were retrieved.
- Capped .NET 11 restore, Release build and tests passed. The fixture suite
  reported 18 passed and one skipped hosted OIDC test. The existing local
  tool pack-and-restore check also passed.
- Both existing smoke configuration checks passed. ShellCheck and workflow
  syntax checks passed, with the hosted `ubuntu-26.04` label supplied to
  the local workflow linter.

## Next live step

Qualify a deployment-owned worker composition. Complete loopback preflight
using the approved external issuer, separate worker registrations and grants,
and deployment-managed ACME material. Public ingress activation requires its
own authorization.
