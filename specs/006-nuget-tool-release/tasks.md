# Tasks

- [x] T001 Configure and verify the main-only `nuget` GitHub environment.
- [x] T002 Extend `tests/verify-tool-package.ps1` to verify and expose the exact packed tool.
- [x] T003 Add `.github/workflows/nuget-release.yml` with verify, login-only and release dispatch modes.
- [x] T004 Validate package identity, isolated installation and workflow guards.
- [x] T005 Verify exact PR and merged CI, run verify and login-only on merged main, and record evidence.

## Acceptance evidence

- The push and pull request runs passed Linux and Windows build, tests and isolated tool installation, as well as Docker and rootless Quadlet on Linux, on heads `a0d491c5e2344b31cfee3aa33243527557c8e6ef` and `f053db899e567d61c351272317cacafb4ee547f4`. [Workflow PR #2](https://github.com/monstertacle/monstertacle.hosting/pull/2) and [Action pin PR #3](https://github.com/monstertacle/monstertacle.hosting/pull/3).
- The first manual runs stopped during runner setup. The correction pins the complete `actions/setup-dotnet` revision. The corrected merge `d6348860962ff1d0b4d59955e369f2a5a0977c2a` passed all four main-branch CI jobs.
- Verify mode on the corrected merge packed, restored and executed `Monstertacle.Hosting.Tool` `0.1.0` from an isolated manifest and uploaded one package artifact. An independent download matched GitHub's archive digest, and the package installed and ran from a fresh local feed. The package SHA-256 was `fcd385f3a4aecc6d22d1318f78c44f592def3f609c7b0b92759dcbd4f1a2de5e`.
- Login-only mode on the same revision exchanged a GitHub OIDC token for a short-lived NuGet key in the `nuget` environment. Its package and publish steps were skipped.
