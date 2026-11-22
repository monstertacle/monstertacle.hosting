# Implementation plan · Static OAuth clients

Extend the existing Bash and jq renderer. Use one client representation for
legacy and manifest input and one set of OAuth settings for realm and client
output. Keep Hosting runtime, contracts, and NuGet outside the change.

Use the current release allowlist and hosted workflows. Compare repeat renders
and reject invalid manifests in the archive fixture. Import the generated
realm into disposable Keycloak and exercise authorization, callbacks, code
redemption and claims with local test users. Preserve Feature 015 checks.

## Constitution check

The change uses shell and public dependencies. It preserves Hosting admission,
policy, and runtime contracts. Public qualification uses illustrative inputs
and disposable credentials. Real credentials, manifests, and account-specific
acceptance remain private.
