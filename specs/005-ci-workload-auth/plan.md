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

## Federation policy contract

The token service must verify the signature against GitHub's discoverable
signing keys and require the exact GitHub issuer, a single configured audience,
and current validity. Use an ordinal match on the signed `repository_id`,
`workflow_ref`, `ref`, and `event_name` claims. Configure exactly one event,
`push` or `workflow_dispatch`. When using an environment, bind its exact
signed name. For a reusable workflow, bind its full `job_workflow_ref`
separately from the caller workflow. For a direct workflow, that claim may
be absent or repeat the direct workflow ref.

Apply a GitHub branch protection rule or ruleset to the designated branch.
Restrict changes to the workflow and its inputs to trusted reviewers and
restrict bypass permissions. When an environment is used, restrict it to the
designated branch and configure any required approvals in GitHub. Matching a
signed branch ref attests which ref ran. Repository protection is a separate
operator control, and the token service does not use runner environment flags
as evidence of protection.

The token service must use the configured workload subject independent of the
GitHub assertion subject, audience equal to the HTTPS MCP resource, the
configured workload scope, a signed `at+jwt` access token with `iat` and
`exp` at most five minutes apart, and a signing authority separate from the
human issuer and GitHub. The acceptance fixture issues a two-minute token and
binds the token exchange request to the configured resource and scope.
