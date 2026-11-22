# Architecture Decision

Keep container deployment lifecycle in Hosting. Hosting is the deployment consumer with coordinated broker and worker persistence, worker identity and leases, a private mTLS topology and a stateless MCP edge. The other reviewed public projects provide libraries or demonstrations. The SharpQrs ASP.NET example binds to loopback and has no independent deployment contract.

Keeping runbooks, manifests, rollout checks and state recovery together lets schema, identity, image and topology changes be qualified against the same revision. A separate repository would require release coordination and a compatibility matrix before a second consumer could use the interface. Revisit extraction when two independent deployment consumers exercise a working, versioned contract with explicit ownership and compatibility rules.

Use a stopped, full-volume archive for each state role. SQLite can keep committed data in a WAL and uses adjacent files for coordination. A private helper can preserve the complete file tree and numeric ownership within the same engine's user namespace. Restore to fresh volume names and compare both trees before any application writer starts. Keep public TLS ingress and issuer workflows under the deployment prerequisites already recorded for chiseled images.
