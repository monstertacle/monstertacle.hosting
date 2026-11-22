# Independent OAuth issuer for direct MCP

## Operator scenarios

1. An operator provisions a dedicated realm on an upstream maintained OAuth
   issuer and registers a public desktop client with an exact loopback callback.
2. The operator qualifies an authorized subject and a separate ungranted subject
   over loopback. The operator enables direct public HTTPS after preflight.
3. A granted user signs in from OpenCode and retrieves a session result from
   two deterministic worker lanes using the public MCP endpoint.

## Requirements

- Require authorization code and S256 PKCE for a statically registered public
  client. Disable public registration of clients and users.
- Require signed access tokens with the canonical HTTPS MCP URI as the sole
  audience and the configured session scope. Hosting grants actions explicitly
  by issuer and stable subject. Model-provider configuration is independent.
- Keep the database, administration, metrics, broker and workers private.
  Allowlist the dedicated realm's browser and OAuth endpoints and login assets
  on the public issuer listener.
- Use the versioned rootless Hosting deployment, Kestrel TLS and broker mTLS.
  Preserve separate persistent state for each deterministic worker lane.

## Acceptance

Verify trusted TLS, protected-resource metadata, issuer discovery and signing
keys, and complete the authorization-code exchange with S256 PKCE before
opening public MCP. Verify 401 for anonymous and wrong-audience callers, 403
for a valid ungranted subject, and result retrieval for both lanes by a granted
subject. Verify public routes and the direct OpenCode connection after
activation. Keep production configuration and qualification evidence private.
