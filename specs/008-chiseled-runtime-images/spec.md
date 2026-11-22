# Feature 008: Chiseled ASP.NET Core runtime images

## Operator scenarios

1. A clean recursive checkout builds the broker, worker and MCP edge from the
   .NET 11 SDK. Each final image runs the same ASP.NET Core entrypoint as a
   non-root user on Ubuntu 26.04 chiseled extra, with ICU, time-zone data and
   default CA roots available.
2. A new Compose or rootless Quadlet deployment starts all three hosts with
   read-only roots and the existing certificate mounts. Broker and worker
   initialize native SQLite databases on their separate state volumes. The
   edge presents trusted HTTPS on loopback, advertises OAuth resource metadata
   and rejects anonymous MCP requests.
3. Operators can diagnose startup and certificate or state issues from the
   host engine. They can provision ingress certificates separately using the
   existing deployment configuration.

## Acceptance

- All three final targets meet the [runtime contract](contracts/runtime-images.md).
- A clean checkout passes the hosted Linux, Windows, Compose and rootless
  Quadlet jobs on the exact pull request revision and again on merged main.
