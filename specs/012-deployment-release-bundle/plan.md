# Implementation Plan: Single-host deployment release bundle

## Delivery and platform

Target Linux x86-64 with Docker Compose v2 or newer, or Podman 5.4 or newer
with rootless Quadlet and a working systemd user manager. Build the three
existing images and a fixture-only Kestrel TLS discovery image. Package
versioned local image references and exact image IDs in the bundle. Distribute
a compressed Docker-format archive and bundle with SHA-256 checksums via
GitHub Releases. Test with both engines. This keeps release consumption
independent of container registry authentication.

## Configuration and acceptance

Render build-free Compose and Quadlet from the existing deployment templates
and add the second worker with its own state and configuration. Provision
private fixture files in a fresh temporary directory. A private-network
HTTPS sidecar serves only OIDC metadata and the public JWKS. Generate signing
material and bearer headers locally. Reuse smoke certificate and Quadlet
rendering code. Use the MCP stateless transport to assert three admission
denials, then retrieve a complete two-lane result and verify both artifact
contents. Run the same acceptance from downloaded assets outside Git.

## Release controls

Run source-free acceptance in CI. Dispatch the independent deployment release
workflow on main and require the existing Build and test workflow to be green
for the exact main SHA. Run both source-free variants against the same assets
before publication. Limit write permission to the publication job. Stage
assets in a draft, verify their hashes before publishing and preserve published
assets on retries. Record qualification and the scoped next live step.

## Constitution check

- Keep the existing .NET 11 projects and public dependencies. Use Bash for
  release and fixture tooling.
- Exercise existing broker authorization, worker claims and result custody
  without changing the protocol or the production identity boundary.
- Use credential-free deterministic acceptance in public CI. Generate fixture
  secrets at runtime, keep them out of images and upload directories, and
  remove them through scoped cleanup.
- Inspect the exact staged diff and run the required staged secret scan before
  each commit.
