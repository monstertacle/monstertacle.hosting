# Feature 017 · Configured model workers

An operator can bind a Hosting lane to a versioned text-model profile without
changing the MCP client, broker protocol, or authorization policy. Two lanes can
use separate profiles, models and credentials. Ordinary model calls use an
HTTPS text chat-completions gateway with an explicitly qualified response contract.

- FR-001: Only private worker configuration may choose the endpoint, model,
  profile revision, instructions, bearer-file reference and output token limit.
- FR-002: Require HTTPS and validated startup configuration. Scope credentials
  to one worker, disable redirects and bound response size. Model output is text.
- FR-003: Only confirmed terminal outcomes release the claim. Ambiguous
  outcomes remain recorded and must not trigger a second model invocation.
- FR-004: Keep the deterministic default and provide opt-in deployment templates
  for Compose and Quadlet in the checksummed public release.
- FR-005: Hosted qualification exercises simultaneous sessions, both lane
  profiles, exact artifact contents and denied MCP callers against a synthetic
  HTTPS gateway. Real-provider qualification uses a private authorized environment.
- FR-006: Keep credentials and site-specific hostnames, addresses, and account
  identifiers out of Git, public diagnostics, release assets and image layers.
