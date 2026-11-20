# Feature 001: Durable session admission and reconnect

## User scenarios and acceptance

1. An allowed caller submits a bounded task to a registered project with a
   mutation UUID. A fresh client can use that UUID or the returned session UUID
   to recover status after reconnect and broker restart.
2. Concurrent identical requests produce one admission and stable job IDs.
   Reusing the mutation UUID with changed input returns a conflict.
3. Missing, expired, wrong-issuer, wrong-audience and invalid-signature tokens
   fail authentication. An unlisted holder of a valid token cannot discover
   tools or read or mutate a session. Two allowed people can access their own
   sessions independently. Cross-owner probes reveal no session data.
4. Revoking a principal, project grant, lane grant or action denies the next
   applicable operation, including replay with an unexpired token. A missing,
   empty, malformed or inaccessible allowlist denies access.
5. An MCP client discovers a configured authorization server through OAuth
   protected resource metadata and uses standard Streamable HTTP tools.
