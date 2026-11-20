# Monstertacle Hosting

Monstertacle Hosting is a .NET 11 control plane and worker host for durable
sessions accessed over MCP Streamable HTTP. The control plane owns admission
and result custody. Workers run registered execution plans.

## Delivery order

1. [Identity, admission and reconnect](specs/001-sessions/spec.md)
2. [Worker coordination and cancellation](specs/002-coordination/spec.md)
3. [Runtime integration and verified results](specs/003-results/spec.md)
4. [Packaging and qualification](specs/004-deployment/spec.md)

The repository uses .NET 11 (`net11.0` and `net11.0-windows` where applicable),
C#, and shell or PowerShell. See the [constitution](.specify/memory/constitution.md).

## Build and test

Install the .NET 11 SDK specified in `global.json`, then run:

```sh
dotnet restore
dotnet build --no-restore -c Release
dotnet test --no-build -c Release
```

The initial solution includes the three host entry points and version 1
admission contract. The current contract tests exercise input limits and repeatable
payload digests. Service admission and reconnect are tracked in the feature plans.
