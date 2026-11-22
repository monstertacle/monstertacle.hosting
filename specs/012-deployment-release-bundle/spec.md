# Feature Specification: Single-host deployment release bundle

**Feature branch** `012-deployment-release-bundle`
**Created** 2026-11-22
**Status** Approved scope

## User stories

### US1 · Install a release on one Linux host (P1)

An operator can download and verify a versioned deployment bundle and image
archive, load the images, and run the broker, MCP edge, and two workers with
Docker Compose or rootless Podman Quadlet. The host needs the selected engine
and ordinary shell tooling. It needs neither a source checkout nor developer,
registry, or model credentials.

**Acceptance**: On Linux x86-64, verify the SHA-256 checksums and load the
archive. From an extracted directory outside the checkout, run the same
quickstart on each engine. Image references must match the archive and no
component may attempt a build or registry pull.

### US2 · Verify the local authorization and execution path (P1)

An operator can run a disposable fixture with private TLS and role-specific
broker mTLS, two worker identities and lane grants, and an ephemeral JWT
credential harness. The edge listens only on loopback. Each worker has its own
volume and deterministic profile. The harness publishes discovery and public
signing material only inside the fixture network and signs assertions locally.

**Acceptance**: Anonymous and wrong-audience session starts return 401.
A signed unlisted subject returns 403. A granted subject starts a session.
Both designated lanes succeed and the verified result and each lane's
deterministic artifact can be fetched through the authenticated MCP edge.
Cleanup removes the fixture's resources, including its private files and
volumes. Fixture names and the port are independent of the local pilot.

### US3 · Publish the verified delivery assets (P1)

Release maintainers can publish a deployment release from a green main revision
using a guarded workflow and a deployment-specific tag. Public assets are
downloadable without authentication, and published bytes remain stable across
retries. NuGet packaging and publication are outside this release path.

**Acceptance**: Verify the exact merged revision, run the release workflow, then
download public assets anonymously, verify them, and run the extracted fixture.

## Scope

Reuse the existing chiseled ASP.NET Core 11 images, deterministic worker and
production deployment templates. Existing Kestrel, external OIDC, ACME, and
deployment-owned agent composition remain the live deployment interfaces.
This stage ends at local acceptance and public distribution of its assets.
