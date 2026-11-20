# Tasks: Durable session admission and reconnect

- [x] T001 Scaffold and author all feature plans before implementation.
- [x] T002 Add .NET 11 solution, versioned contracts and GitHub CI.
- [x] T003 Implement exact JWT validation, reloadable fail-closed allowlist and
  per-action grants.
- [x] T004 Implement transactional admission, scoped replay, lookup, status
  and bounded list through authenticated broker HTTP.
- [ ] T005 Implement and publish the transport-only Communication MCP adapter,
  then wire MCP host and public OAuth resource metadata.
- [x] T006 Verify two allowed people, valid unlisted person, credential rejection,
  cross-owner denial, concurrent replay, revocation and restart.
- [ ] T007 Verify MCP discovery, initialize, tool calls and fresh-client
  reconnect against the broker and publish the verified slice.

The private HTTPS acceptance uses generated client and server certificates and
a deterministic discovery and signing-key fixture. It exercises the production
broker middleware, handlers and SQLite store. MCP and external identity
qualification remain tracked separately.
