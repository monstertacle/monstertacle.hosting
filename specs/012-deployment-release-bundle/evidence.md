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

## Hosted CI and public release · 2026-11-22

- [PR #12](https://github.com/monstertacle/monstertacle.hosting/pull/12)
  merged after both the [push run](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45032024009)
  and the [pull request run](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45032027997)
  passed for head `b0c51107e494b3d8c45160493a517ffcb597b75a`.
- [Main CI](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45032201835)
  passed for merge commit `b413a601943795cb32f2dd1d6410852f932fe840`.
- The [deployment release run](https://github.com/monstertacle/monstertacle.hosting/actions/runs/45032413622)
  built that revision, qualified the same archives on native Docker Compose
  and rootless Quadlet, and published
  [deploy-v0.1.0](https://github.com/monstertacle/monstertacle.hosting/releases/tag/deploy-v0.1.0)
  at 14:24:02 UTC.
- An anonymous HTTPS download returned HTTP 200 for each of the three
  published assets. The downloaded `SHA256SUMS` digest was
  `dabee28ac3eba0b145d8ec7e9b9b591abff8b2a3768e3dfe76def89e25bf9359`.
  Both archive checksums and the bundled revision matched. The downloaded
  images loaded on local rootless Podman 5.4.2. From the extracted bundle,
  Quadlet repeated all three admission denials, admitted a granted JWT,
  and returned the verified result and both deterministic artifacts.
- The existing native Quadlet lifecycle smoke passed. A malformed checksum
  file was rejected before image load.

## Next live step

Qualify a deployment-owned worker composition. Complete loopback preflight
using the approved external issuer, separate worker registrations and grants,
and deployment-managed ACME material. Public ingress activation requires its
own authorization.
