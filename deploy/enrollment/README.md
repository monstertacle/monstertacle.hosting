# Operator-managed human access

Use the [deployment release](../README.md) on Linux x86-64 with Docker Compose
or rootless Podman Quadlet. Load the verified images before running enrollment.
Configure the same trusted human issuer, HTTPS MCP resource audience and OAuth
scope in `broker.env` and `edge.env`. The issuer supplies the exact access-token
`iss` and opaque stable `sub`. Obtain this pair from a trusted administrator or
verified issuer records. Display names and email addresses can change.

## Share the policy directory

Under the operator-owned configuration directory (mode `0700`), create a
`policy` subdirectory and place the canonical document at `policy/policy.json`.
Start with [the example](policy.example.json) and set the actual project and
lane IDs. To migrate an installation, reconcile its existing policies before
selecting the canonical file. Keep existing stable person IDs, workload policy
and grants. The service UIDs must be able to read the file inside their
containers. For example, use mode `0755` on the policy directory and `0444` on
the file under the private configuration directory.

With Compose, add `-f enrollment/compose.yaml` after `-f compose.yaml`.
With Quadlet, put the two `.container.d` directories under the same user
configuration directory as the corresponding units. Run
`systemctl --user daemon-reload` and restart the broker and edge when first
switching the policy mount. Both services then open the current policy on each
read, so enrollment requires no restart.

## Enroll or revoke

Create a local request file with mode `0600`, using the following structure.

```json
{
  "operation": "enroll",
  "id": "example-operator",
  "issuer": "https://identity.example.net/realms/hosting",
  "subject": "opaque-example-subject",
  "grants": [
    { "project": "example", "lane": "primary", "actions": ["start", "read"] }
  ]
}
```

Run the same command for the first person and each additional person. Select
`podman` for Quadlet.

```sh
bash enrollment/enroll.sh docker "$HOSTING_CONFIG_DIR" ./private-request.json
```

Supported actions are `start`, `read`, `cancel`, `result`, `artifact`, and
`accept-result`. Starting a session requires `start` on every lane in the
project's plan. Repeat the exact assignment safely. A different identity under
the same ID, a different ID for the same identity, or different active grants
fails with the current policy intact.

To revoke a single-identity principal, submit the same ID, issuer and subject
with `"operation": "revoke"` and omit `grants`. This removes its grants and keeps
its stable ID and identity binding. A subsequent explicit grant can restore
access for the same principal. Legacy principals with multiple identities
require a separately coordinated policy edit. Keep policy requests private and
coordinate any other writers using the same directory lock. Revocation is
checked on subsequent requests and does not terminate work already dispatched.
