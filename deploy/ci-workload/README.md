# GitHub Actions workload access

Provision a single workflow and a single Hosting project with an external
security token service (STS). The example uses the GitHub.com issuer and the
OAuth 2.0 token exchange grant defined in RFC 8693. Hosting's loopback authority
is a test fixture. Production requires an independently operated STS, HTTPS
endpoints and live qualification of any human OAuth clients in use.

## Configure the trust

Copy `hosting-submit.yml` to `.github/workflows/hosting-submit.yml` in the
designated repository. Protect the branch with a GitHub ruleset or branch
protection rule. Require review of workflow and task changes, restrict bypass,
and restrict the `hosting-ci` environment to that branch. Add environment
reviewers if required by the operator's release policy. A signed `ref` claim
identifies the branch that ran. GitHub repository settings enforce protection.

In the STS, verify the GitHub JWT signature against the issuer's HTTPS
discovery and JWKS, the validity window, and a single exact audience. Use exact
matches for the following verified values, replacing the example repository:

| Claim | Required value |
| --- | --- |
| `iss` | `https://token.actions.githubusercontent.com` |
| `aud` | `urn:example:hosting:ci-exchange` |
| `repository_id` | The designated repository's numeric GitHub ID |
| `workflow_ref` | `owner/repository/.github/workflows/hosting-submit.yml@refs/heads/main` |
| `ref` | `refs/heads/main` |
| `event_name` | `workflow_dispatch` |
| `environment` | `hosting-ci` |
| `job_workflow_ref` | Absent or the same full direct workflow ref |

For a reusable workflow, also require the configured full reusable workflow ref
in `job_workflow_ref`, ideally pinned to a reviewed commit. Require the caller
workflow and branch claims independently. Use one explicitly trusted event and
exclude both pull request events. Derive authority from the verified claims.

Accept a form POST at the configured HTTPS token endpoint with
`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`,
`subject_token_type=urn:ietf:params:oauth:token-type:jwt`, the GitHub assertion
in `subject_token`, and
`requested_token_type=urn:ietf:params:oauth:token-type:access_token`. Match
`resource` and `scope` to the provisioned HTTPS MCP URL and workload scope.
Return `access_token`, `issued_token_type`, `token_type=Bearer`, and
`expires_in` between 1 and 300 seconds. If returning `scope`, return the
requested scope exactly.

Issue a signed `at+jwt` with the configured workload issuer and subject,
the exact MCP URL as its sole `aud`, scope, `iat` and `exp` within five
minutes. Publish HTTPS OIDC discovery and signing keys for this issuer. Use
signing authority separate from GitHub and the human provider.

## Configure Hosting and the job

Keep the existing human issuer settings. On the edge and broker, use the exact
same MCP audience and set the additional workload options in the respective
service environment:

```dotenv
Mcp__Authentication__Workload__Issuer=https://workload-identity.example
Mcp__Authentication__Workload__Scope=ci-sessions
Broker__Authentication__Workload__Issuer=https://workload-identity.example
Broker__Authentication__Workload__Scope=ci-sessions
```

Merge the [policy sample](policy.example.json) into both current policy files.
Replace the sample subject and expiration with the operator's unique
provisioning identity and a bounded UTC deadline. Grant only `start` and
`read` to the workload. Admission requires both on every lane configured for
the assigned project. The workload can recover its own sessions. To revoke it,
remove the workload entry from both policy files by atomic replacement and
disable the corresponding STS binding. Provision new access with a new subject
and workload ID. Policy revocation denies new requests and worker dispatch
authorizations, including retries with previously issued tokens.

Pack a reviewed source revision with
`dotnet pack src/Monstertacle.Hosting.Tool -c Release` and publish its package to
an operator-controlled HTTPS package feed accessible from the runner. The
example installs version `0.1.0` from that feed as a local tool. Set these
GitHub variables:

| Variable | Example |
| --- | --- |
| `HOSTING_REPOSITORY_ID` | Numeric ID as a repository variable |
| `HOSTING_MCP_URL` | `https://hosting.example/mcp` |
| `HOSTING_TOKEN_URL` | `https://workload-identity.example/token` |
| `HOSTING_ASSERTION_AUDIENCE` | `urn:example:hosting:ci-exchange` |
| `HOSTING_SCOPE` | `ci-sessions` |
| `HOSTING_PROJECT` | `demo` |
| `HOSTING_TOOL_SOURCE` | `https://packages.example/v3/index.json` |

Submit with a stable UUID and reuse that UUID with the same task on retry.
Use `lookup` with that UUID if the first response is lost. The workflow
prints session state as JSON and gives only its submit job permission to mint
a GitHub assertion.

## Public endpoints and verification

The edge exposes process liveness at `GET /health/live` and human OAuth
protected-resource metadata at
`GET /.well-known/oauth-protected-resource/mcp`. Require a bearer token
for `/mcp` before forwarding transport methods. A reverse proxy must preserve
the bearer header, forward to the edge over HTTPS, and avoid rewriting an
unauthenticated request onto a privileged route. OPTIONS never runs MCP
operations.

The main-only **Hosted OIDC smoke** uses GitHub's issuer and a disposable
loopback STS, edge and broker. Its result qualifies that fixture. Production
qualification requires an operator-managed issuer, protected repository and
environment, the deployed MCP route, and any live human OAuth clients.
