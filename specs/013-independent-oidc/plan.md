# Implementation plan

Use the ingress preflight from feature 010 and the deployment release from
feature 012. Run the identity service with durable PostgreSQL storage on a
private network and pin official image digests. Expose its dedicated realm
through an HTTPS allowlist proxy. Keep Hosting on its canonical HTTPS /mcp URI.

Register the public desktop client with the exact callback for the deployed
client release. Set the sessions scope and attach an access-token audience
mapper. Inspect actual issued tokens for default audience claims. Provision
users privately, including a temporary test pair. Derive Hosting grants from
subjects returned by the issuer.

## Qualification and rollback

Use loopback mappings for trusted TLS at both canonical origins. Check the
issuer surface, a successful code exchange, failed exchange with the wrong
verifier, the MCP denial matrix and a complete two-lane result. Activate the
issuer listener first and verify restricted routes. Run the ingress preflight
before activating the MCP listener. Repeat the qualification against public
ingress and exercise OpenCode with its static client ID.

Disable the dedicated MCP listener before disabling the issuer listener on
rollback. Keep scoped data for recovery. After acceptance, remove test
identities and grants and retain the operator's provisioned identity. Maintain
certificate renewal, PostgreSQL backup and restart procedures privately.

## Constitution check

This is a configuration and qualification slice using the existing .NET 11
artifacts. Keep production credentials and tests that handle them outside
source. Use a staged secret scan before committing the generic specification.
