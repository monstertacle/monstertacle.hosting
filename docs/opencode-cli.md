# Use Hosting from OpenCode CLI

Install OpenCode on your workstation using the [official installation guide](https://opencode.ai/docs/).
Get the Hosting HTTPS hostname, project ID and an approved task from your
Hosting operator. Your workstation must trust the hostname's TLS certificate
chain. If your organization uses a private CA, follow its trust-store procedure.
Use the corporate account approved for Hosting with grants for the project.

## Connect and sign in

From the project directory where you use OpenCode, run the following commands.
Replace `mcp.example.com` with the hostname from your operator and keep the
`/mcp` path. `monstertacle` is a local alias. If you choose a different name,
use it in the authentication command too.

```sh
opencode mcp add monstertacle --url https://mcp.example.com/mcp
opencode mcp auth monstertacle
opencode mcp list
```

Complete the MCP sign-in in your browser. This OAuth flow uses your Hosting
account. OpenCode's model provider has its own credentials. Check that
`opencode mcp list` shows `monstertacle` as connected, then launch OpenCode
from the same directory.

If your operator requires a pre-registered OAuth client, use the
operator-provided settings. See the [OpenCode MCP documentation](https://opencode.ai/docs/mcp-servers/).

## Submit and recover a session

Use the shared [session prompts and recovery steps](codex-cli.md#submit-and-recover-a-session)
for submitting an approved task, saving its identifiers and retrieving results.
