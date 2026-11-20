# Implementation plan: Embedded runtime and verified results

Reference version 0.2.0-alpha.10 of the public Multisession Common,
Storage.Sqlite, Linux and Windows packages. Use OS-specific state directory
leasing and one in-process SessionRuntime per configured worker scope. Use
registration and durable runtime request identity for native recovery. Fixture
registrations exercise the same runtime contract without credentials.

Bind result submission to the claimed job and worker identity. Atomically
store bounded immutable artifact bytes and canonical verified manifests.
Expose opaque IDs and bounded reads after current policy checks. Preserve
explicit acceptance as a scoped mutation against a manifest digest.

Public package registry:
[Common](https://www.nuget.org/packages/Monstertacle.Multisession.Common/0.2.0-alpha.10),
[Storage.Sqlite](https://www.nuget.org/packages/Monstertacle.Multisession.Storage.Sqlite/0.2.0-alpha.10),
[Linux](https://www.nuget.org/packages/Monstertacle.Multisession.Linux/0.2.0-alpha.10),
[Windows](https://www.nuget.org/packages/Monstertacle.Multisession.Windows/0.2.0-alpha.10).

Constitution check: public package restore, broker custody, explicit evidence
and deterministic fixtures.
