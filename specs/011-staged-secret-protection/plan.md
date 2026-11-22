# Implementation plan

Use one small Bash entry point in each repository with official Gitleaks
8.30.1 and its default rules. Verify the release archive before installation.
Resolve the Git root, scan only the index, redact values and fail closed.
Document manual invocation and optional pre-commit hook integration for Linux
and Git Bash on Windows. GitHub Secret Scanning and Push Protection provide
the server-side checks for supported patterns.

Amend each constitution using its own conventions. The amendment reduces the
risk of publishing credentials and private state by requiring review of the
index and a scan before each commit. Runtime contracts and native test and
coverage gates are unaffected. Validate the scanner in a disposable private
repository, and verify existing CI or reproduce local checks where CI is
unavailable. Keep verification evidence private.

Constitution review: use Bash and public dependencies, keep secret material
outside public artifacts and preserve independent credential-free acceptance.
