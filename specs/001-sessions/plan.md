# Implementation plan: Durable session admission and reconnect

Use .NET 11 and a versioned private HTTP contract. Run a single authoritative
broker using SQLite transactional admission. Persist principal, original
issuer/subject, project, canonical request digest, mutation UUID, session UUID
and registered lane/job identities before making work visible. Scope mutation
UUIDs by operation, principal and project. Bound task bytes and session lists.

Use TLS and separate service authentication on private routes. Forward the
caller credential only over the authenticated edge-to-broker hop. Validate JWT
signature, exact issuer and audience, expiry and scope at both boundaries.
Resolve an explicit issuer/subject mapping on every call. Validate the whole
configuration before honoring any entry. Apply per-action and per-lane grants
before reading cached receipts. Also require relevant read grants for receipt
and status data.

Use an official MCP SDK adapter in Communication with a narrow operations
interface. Hosting supplies a scoped client and owns authentication, authorization
and OAuth resource configuration. Keep application reconnection independent of
the MCP transport session by using durable IDs. Protected resource discovery is
anonymous while MCP tool access is gated before SDK dispatch.

Constitution check: public packages, independent authority, durable recovery
and deterministic, credential-free acceptance.
