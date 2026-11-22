# Feature 005 — GitHub Actions workload access

An operator can provision one GitHub Actions workload to submit sessions and
read its own status for one Hosting project. Human OAuth and session ownership
continue to use the existing policy and session identifiers.

## Requirements

- A version-pinned .NET 11 local tool supports `submit`, `status`, and
  `lookup`, emits JSON, and accepts a caller-supplied UUID for idempotent
  submission and recovery. Each command has a finite deadline.
- The tool requests GitHub's job assertion for a configured audience and
  exchanges it at an explicitly configured HTTPS endpoint. The resulting
  short-lived OAuth access token is sent only to the configured HTTPS MCP
  resource. Authentication has no interactive mode. Authenticated requests
  reject redirects, and routine diagnostics omit tokens and response bodies.
- Federation validates GitHub's signature and exact issuer and assertion
  audience, then matches the repository ID, full workflow ref, protected branch
  ref, event, and any configured environment or reusable workflow ref.
  Pull request events are ineligible.
- Edge and broker independently validate the OAuth access token and distinguish
  the provisioned workload from human identities. Fresh policy checks enforce
  expiration, revocation, project, lanes, action and session ownership, including
  on receipt recovery and dispatch.
- Anonymous MCP transport requests receive an explicit denial before protocol
  handling. Public access is limited to protected-resource metadata and process
  liveness. OPTIONS cannot invoke MCP handlers.
- Acceptance uses the same authenticated HTTPS routes and configuration for
  positive and negative checks. Denied submissions leave no durable admission.
  Test fixtures cover bad signatures, expiry, wrong issuer/audience, wrong
  workflow in the same repository, unlisted and revoked access, and cross-scope
  requests.

## Delivery

Ship sample policy and tool configuration, runnable workflow, package restore
test, and credential-free integration tests. Run the Linux and Windows suite
and Linux Docker and rootless Quadlet smoke checks on the exact PR and main
revisions. Qualify authentic GitHub OIDC in a protected workflow when available.
A production token service, protected GitHub workload and live human clients
require operator configuration and independent live qualification.
