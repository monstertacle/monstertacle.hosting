# Use Hosting from Codex CLI

Run Codex on your workstation. Get the Hosting HTTPS hostname, project ID,
and an approved task from your Hosting operator. Your corporate OAuth
account needs a Hosting identity and project grants. Your workstation must
trust the certificate chain for the Hosting hostname. If your organization
uses a private CA, install it using your organization's trust-store procedure.

## Connect and sign in

Replace `HOST` with your Hosting hostname.

```sh
codex mcp add monstertacle --url https://HOST/mcp
codex mcp login monstertacle
codex mcp list
```

Adding the server may start OAuth automatically. If sign-in completes during
`add`, continue to `list`. Sign in with the corporate account that has the
Hosting grants. Start Codex and type `/mcp` to check the active connection and
available tools. If Codex was already running when you changed its MCP
settings or signed in again, restart it and check `/mcp`.

If your OAuth operator provides a pre-registered client ID, use this form of
the add command.

```sh
codex mcp add monstertacle --url https://HOST/mcp --oauth-client-id CLIENT_ID
```

From a terminal without a browser, use `codex mcp login monstertacle --no-browser`.
Open the printed authorization URL in a browser, sign in, and paste the full
redirect URL back into the terminal when prompted. Follow your OAuth operator's
instructions for the registered callback. Keep authorization and redirect URLs
out of chat. The flags above were checked with Codex CLI 0.171.0. See the
[official Codex MCP guide](https://developers.openai.com/codex/mcp).

## Submit and recover a session

Generate a fresh UUID for each new task, for example with `uuidgen`. Save
that value, the project ID and the exact task before submitting. Save the
returned `sessionId` (SessionId) alongside them. Substitute your values in
these example prompts.

> Use Hosting's `session_start` for project PROJECT_ID with mutation ID
> MUTATION_UUID and task “Summarize the approved project brief.” Return the
> session ID and the job states.

> Check the Hosting session SESSION_ID with `session_status`. Show each job's
> lane and state.

> Recover my Hosting session with `session_lookup` using project PROJECT_ID
> and the original start mutation ID MUTATION_UUID. Return its session ID.

Use lookup if Codex restarts or loses the submit response. Retry a submit
only with the original mutation UUID, project and exact task. Reuse is bound
to the same Hosting identity and project. Then request the result and an
artifact using the IDs and name from the manifest.

> Use `session_result` to show the verified result for SESSION_ID, including
> whether the manifest is complete and which artifacts each job produced.

> Use `session_artifact` to retrieve artifact ARTIFACT_NAME from job JOB_ID in
> session SESSION_ID. Report its SHA-256 and byte count.

| Tool | Arguments | Returns |
| --- | --- | --- |
| `session_start` | `mutationId`, `project`, `task` | Session ID, project and job states |
| `session_status` | `sessionId` | Current session and job states |
| `session_lookup` | `mutationId`, `project` | The session admitted under the original start mutation |
| `session_result` | `sessionId` | Verified aggregate digest and child manifest and state when available |
| `session_artifact` | `sessionId`, `jobId`, `name` | Bounded base64 content and descriptor with byte count and SHA-256 |

Hosting checks current grants by project and lane, along with session
ownership. Submitting requires `start` on each configured lane. Status and
lookup require `read`. Result reads require `read` and `result` on every child
lane. An artifact read requires `read` and `artifact` on the artifact's lane.
If a tool is available in `/mcp` but Hosting denies a call, ask your operator
to verify your identity, session ownership and grants.

## CI workloads

The published `Monstertacle.Hosting.Tool` .NET tool is used for the separate
GitHub Actions workload flow. It exchanges a GitHub OIDC assertion and uses a
workload policy scoped to the admitted workload. Corporate OAuth through
Codex uses a person identity and its own project grants. CI scope is
intentionally distinct and the workload grant leaves human access unchanged.
See the [CI workload guide](../deploy/ci-workload/README.md).
