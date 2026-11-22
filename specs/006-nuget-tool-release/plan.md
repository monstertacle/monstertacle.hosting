# Implementation plan

- Extend the existing PowerShell package verifier to check the effective MSBuild identity and inspect the nuspec. Use the same packed file for isolated restore, help verification and the workflow artifact.
- Build and validate with the repository's .NET 11 SDK selection and recursive public submodules. Upload a single immutable artifact and pass its ID and package digest directly to the following job.
- Restrict execution to manual dispatch on `refs/heads/main` in `monstertacle/monstertacle.hosting`. Use the `nuget` GitHub environment with a branch-only `main` policy. Only the separate environment job can request an OIDC token. The official NuGet action exchanges it for a short-lived key.
- Qualify the PR and merged commit using public CI, then exercise verify and login-only on the merged commit. Record observable results.

The design follows the constitution through public dependencies, narrow credential custody, immutable evidence tied to the run, and separate deterministic package verification and live identity exchange. The workflow does not modify the NuGet policy.
