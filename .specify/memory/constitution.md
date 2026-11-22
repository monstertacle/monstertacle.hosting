# Monstertacle Hosting Constitution

## I. Public dependencies and platform baseline

Host and test projects target .NET 11 exclusively (`net11.0` and
`net11.0-windows` where required). Use C#, shell or PowerShell for development
and verification. Python is excluded from product and development tooling.
Build from a public checkout using versioned public dependencies.

## II. Authority and custody

The control plane owns durable session and job admission, current policy and
result custody. Authenticate caller and service separately. Deny access when
identity, configuration or grants cannot be verified. Check existing receipts
and artifact reads against current authorization. Keep credentials, request
content and mutable runtime state out of source and routine diagnostics.

## III. Protocol and recovery

Version contracts. Keep MCP adaptation separate from policy and execution.
Persist identifiers and mutation receipts before dispatch. Reconcile uncertain
starts against immutable identities and recorded runtime evidence. Expose
cancellation intent, observed stop and uncertainty distinctly.

## IV. Independent acceptance

Order implementation as bounded, independently verifiable vertical slices.
Run credential-free deterministic tests in public CI. Record native and live
client qualification separately and apply deployment credentials only in an
authorized environment.

## V. Secret handling

Keep real API keys, tokens, passwords, credential-bearing connection strings,
private keys, confidential data, and session or authentication state out of
sources, tests, docs, examples, Git history, logs, artifacts, caches and images.
Keep actual deployment hostnames, network addresses, and account identifiers
out of Git and public artifacts. Use generic placeholders or synthetic fixture
identifiers in public materials.
Use runtime secret management and generate ephemeral synthetic test keys at test
time. Ignore rules alone are insufficient. Report confirmed exposure privately
to the authorized owner without values and rotate affected real credentials
promptly with authorization.

Before every commit, examine the exact staged diff for secrets and actual
deployment details, and run the [staged secret check](../../docs/secret-handling.md).
A detection, missing dependency or scan failure blocks the commit. Reports must
redact sensitive values.

## Governance

Review each feature against these principles before implementation. Record an
approved amendment in Git with its rationale and applicability.

The 2026-11-22 amendment applies to all contributions to this public repository.
It adds pre-commit verification to reduce accidental disclosure and leaves
runtime contracts unchanged. See [the rollout plan](../../specs/011-staged-secret-protection/plan.md).
