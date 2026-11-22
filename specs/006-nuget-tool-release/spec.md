# Hosting tool release

A maintainer can verify or release the exact `Monstertacle.Hosting.Tool` version from `main` in the canonical Hosting repository. The operator must provide the expected version. A mismatch with the project or package fails the run.

- **Verify** is the default. Pack once, restore from an isolated local manifest and feed, run the installed tool's help, and retain the verified package as a workflow artifact.
- **Login only** checks whether NuGet accepts the GitHub workload identity for the `nuget` environment. The job ends after exchanging its OIDC token.
- **Release** requires explicit selection. An environment-scoped job retrieves the exact artifact from the current run, checks its SHA-256 against the verified package, and submits that single file to nuget.org. A duplicate push fails.

Acceptance covers the exact PR and merged revision through the Linux and Windows build and tool jobs, the Docker and rootless Quadlet jobs, and safe manual verification and login-only dispatches on merged `main`. First-release qualification holds back the release dispatch.
