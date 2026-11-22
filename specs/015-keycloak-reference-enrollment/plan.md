# Implementation plan for the Keycloak reference

## Design

- Package an independent reference Compose stack, private database and admin
  access, and an allowlisted TLS proxy for one realm.
- Render the realm from explicit resource and callback settings. Use static
  public authorization code clients with S256 and a dedicated audience mapper.
- Ship a Bash helper for private Admin REST calls. Consume secrets through
  protected files or stdin and hand off the exact subject to feature 014.
- Exercise the flow with ephemeral passwords and TLS for the reference. Assert
  actual token claims, denial paths, lifecycle and public endpoint boundaries.

## Constitution check

Provider-specific provisioning stays in the optional reference. The Hosting
runtime uses its existing issuer, audience, scope and grant checks. Private
state and generated secrets stay outside tracked files and public artifacts.
