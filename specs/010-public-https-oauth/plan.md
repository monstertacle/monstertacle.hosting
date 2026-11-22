# Implementation plan

## Design

Reuse the existing chiseled edge and token validation policy. Add an opt-in public deployment startup gate that loads the serving configuration, verifies the exact audience and provisioned human grants, and obtains usable issuer signing keys through the existing HTTPS discovery path. Pair it with host preflight for the external DNS answers, exclusive loopback binding and directly observed Kestrel certificate. Require readiness evidence before arming the systemd socket.

Provide example IPv4 socket and sandboxed TCP forwarding units, configuration and a runbook. Keep the broker and worker on existing private paths. Use a bounded ACME staging attempt on the deployment host. Attempt production issuance only after staging proves control.

Qualify locally using isolated names, private network and state volumes, and an ephemeral local issuer and CA. Use exact issuer, resource and subject inputs and verify an authenticated protocol result independently of anonymous denial. Run negative checks for mismatched DNS, certificate or publication, unusable discovery, missing grants and the disabled state.

## Constitution review

Build and test with .NET 11, public dependencies and Bash. Reuse current authority and service authentication. Keep credential material outside the source tree and public CI artifacts. Preserve existing MCP contracts and durable state. Keep the deployment preflight and credential-free CI distinct from native and live acceptance.
