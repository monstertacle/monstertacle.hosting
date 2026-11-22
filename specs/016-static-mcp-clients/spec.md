# Feature 016 · Static OAuth clients for remote MCP

An operator can register a desktop client and independent chat clients against
one remote MCP resource using the optional Keycloak reference from Feature 015.

- Keep the legacy single-client renderer and default callback. Accept an
  explicit private manifest for multiple clients. Validate IDs, callback
  types and exact redirect URIs before emitting any payload.
- Require authorization code and S256 PKCE for all clients. Preserve the
  stable user subject and the exact MCP resource audience with the
  `sessions` scope. Support public and confidential registrations.
- Render a realm for initial import or client payloads for private manual
  application to an existing compatible realm. Keep secrets and deployment
  inputs outside Git and the release bundle.
- Provide fixed examples and setup instructions for OpenCode, ChatGPT and
  Claude. Actual chat sign-in is a separate account-specific acceptance step.
- Qualify the public archive with native Compose and Quadlet. The Compose
  Keycloak fixture exercises static clients without third-party services.
