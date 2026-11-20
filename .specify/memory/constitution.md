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

## Governance

Review each feature against these principles before implementation. Record an
approved amendment in Git with its rationale and applicability.
