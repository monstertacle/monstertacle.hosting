# Shared OAuth client deployment

An operator can configure a shared public OAuth client from a private manifest
on first install and apply the same manifest to a compatible existing realm.

- Allow a single public registration with explicit HTTPS and IPv4 loopback
  callbacks, required S256, authorization code support and the default
  `sessions` scope bound to the exact MCP resource.
- Reapply unchanged settings without writing. Update callbacks on a compatible
  registration and refuse conflicting identities or unexpected permissions.
- Publish OAuth discovery that advertises public token exchange and omits
  dynamic registration. Retain the public proxy's route restrictions.
- Qualify the extracted release with synthetic client details in hosted CI.
  Keep deployment coordinates, credentials and site state external.
