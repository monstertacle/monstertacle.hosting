# Optional Keycloak reference deployment and human account enrollment

**Feature branch** `feature/hosting-enrollment`
**Created** 2026-11-22
**Status** Approved scope

## User stories

### US1 · Set up an optional issuer

An operator can bring up a Keycloak realm in a separate Docker Compose project
with PostgreSQL, official pinned images, and operator-supplied TLS. The public
listener defaults to loopback and serves the realm's OIDC and login endpoints.
Admin, database, and management access remain private.

**Acceptance.** A static public registration accepts the authorization code
flow with S256 PKCE and an exact callback. Access tokens carry the exact realm
issuer and resource audience and include the `sessions` scope. Self-registration,
open dynamic registration, and password grant are disabled.

### US2 · Provision and grant a human account

An operator with restricted realm permissions provisions a user, confirms the
stable subject through the private admin interface, and passes the issuer and
subject to the provider-neutral Hosting enrollment tool. The same procedure
works for the first and subsequent people. An interrupted handoff leaves the
account without Hosting permissions. Recovery uses the known stable user ID.

**Acceptance.** Two distinct users obtain distinct subjects and independent
permissions. Unlisted users are denied despite valid tokens. Revoking Hosting
grants takes effect for existing tokens. Normal command arguments, output and
logs contain no credentials.

### US3 · Verify the optional reference from a release

The source-free Linux x86-64 deployment release includes the reference
templates, helper and runbook. The operator can keep using a different issuer.

**Acceptance.** Verify actual token claims and PKCE failures with disposable
state. Confirm public denial of admin access. Document that the Keycloak
reference is verified on Compose and uses separately obtained upstream images.
