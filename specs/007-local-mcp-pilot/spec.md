# Local MCP pilot

A person with a Hosting project and a corporate OAuth identity can connect
Codex CLI, submit an approved task, recover the same Hosting session from a
fresh client, and read the result and its artifacts.

## Acceptance

1. The guide gives the connection commands, describes TLS trust and project
   grants, names the tool arguments, and shows how to save and reuse the
   original mutation and session IDs.
2. A local rootless Podman run exercises the actual HTTPS edge and private
   mTLS broker. Separate deterministic workers on both lanes complete work
   and publish artifacts whose bytes match the broker's descriptors.
3. Fresh MCP client initialization and lookup recover the existing admission.
   Missing or invalid credentials, an unlisted identity, and an unregistered
   broker client certificate are rejected. An allowlisted second owner cannot
   read or change the first owner's session or create work in a denied
   project. Broker state confirms denied mutations left no admission or job.
4. The local acceptance uses disposable credentials and state. Live corporate
   OAuth, web clients and deployment-owned worker inputs have separate
   acceptance. CI workload grants retain their existing scope.
