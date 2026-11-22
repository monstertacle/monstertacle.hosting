# Use Hosting from Pi CLI

Install Pi on your workstation using the [official installation options](https://pi.dev/).
For the npm installation of Pi 1.6.0, use Node.js 22.19.0 or later.
Get the Hosting HTTPS hostname, project ID and an approved task from your
Hosting operator. Your workstation must trust the hostname's TLS certificate
chain. If your organization uses a private CA, follow its trust-store procedure.
Use the corporate account approved for Hosting with grants for the project.

## Connect and sign in

Replace `mcp.example.com` with the hostname from your operator and keep the
`/mcp` path. `monstertacle` is a local alias. If you choose a different name,
use it in the login command too.

```sh
pi mcp add monstertacle --url https://mcp.example.com/mcp
pi mcp login monstertacle
pi mcp list
```

Complete the MCP sign-in in your browser. This OAuth flow uses your Hosting
account. Pi's model provider has its own credentials. Check that
`pi mcp list` shows `monstertacle` as connected. Start Pi, type `/mcp`
and select the server to inspect its tools.

If your operator requires a pre-registered OAuth client, use the
operator-provided settings. See the [Pi MCP documentation](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/mcp.md).

## Submit and recover a session

Use the shared [session prompts and recovery steps](codex-cli.md#submit-and-recover-a-session)
for submitting an approved task, saving its identifiers and retrieving results.
