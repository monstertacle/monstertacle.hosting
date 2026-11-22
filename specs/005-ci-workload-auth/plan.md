# Implementation plan

1. Add an optional, separate workload token issuer to edge and broker. Use exact
   issuer and MCP audience matching at both. Extend the current policy file with
   one workload entry with its own stable principal, issuer/subject, expiry and
   project-specific start/read grants. Retain the existing durable ownership
   model and recheck the current policy during worker dispatch. Cover all MCP
   transport methods with an authentication gate before request dispatch.
2. Integrate an external operator-managed RFC 8693 security token service.
   Provide an executable matching policy and an ephemeral test-only authority.
   Bind the assertion audience to the STS and the access-token audience to the
   MCP resource. Require a protected branch and an exact verified workflow ref.
   The STS maps a verified match to a provisioned subject with a short lifetime.
3. Pack a local .NET tool that obtains the GitHub assertion, exchanges it over
   HTTPS, and uses the assigned access token to call existing session tools.
   Document the ownership key, project, endpoint, scope, mutation UUID, and
   workload claim inputs. Ship a GitHub workflow with job-scoped OIDC permission.
4. Exercise real HTTPS admission, status, lookup and denied operations against
   the same fixture. Verify durable denial, fresh revocation, pack and restore,
   and hosted Linux/Windows and container checks before merge. Exercise genuine
   GitHub OIDC in a disposable protected-main context where available.

## Constitution check

All projects and checks use .NET 11, C#, shell or PowerShell and public
dependencies. Authentication and admission remain separate from MCP transport.
Existing durable mutation receipts control retries. CI checks use disposable
keys and policy, with live deployment and human clients qualified separately.

## Protocol references

- [RFC 8693 OAuth 2.0 Token Exchange](https://datatracker.ietf.org/doc/html/rfc8693)
- [GitHub Actions OIDC claims](https://docs.github.com/en/actions/reference/security/oidc)
- [GitHub issuer discovery](https://token.actions.githubusercontent.com/.well-known/openid-configuration)
