# Optional Keycloak issuer for Hosting

This Compose project runs Keycloak in production mode with PostgreSQL. The
Hosting broker and edge keep using the standard issuer, audience, scope and
policy settings. The reference uses separately downloaded upstream images.
The Hosting image archive covers Docker Compose and rootless Podman Quadlet.
This issuer reference is for Docker Compose on Linux x86-64.

## Prepare private configuration

Choose distinct HTTPS login and private admin origins, for example
`https://identity.example.net:9443` and `https://identity.example.net:9444`.
Use a TLS certificate trusted by browsers, the operator's tools, and the
Hosting containers. The certificate must cover both origins' hostnames.
The example listeners both bind to loopback. In a loopback test, resolve the
example hostname to loopback on the workstation. Use resolvable names and an
operator-managed TLS route for remote login. Route external traffic only to
the restricted login listener and keep admin behind local access or a
private tunnel. The database, Keycloak HTTP connector and management
interface have no published port.

Set the paths and origins from the extracted release directory.
Preserve the verified archive's file modes during extraction. If your
extraction tool applies a restrictive umask, restore mode `0755` on
`keycloak/init-database.sh` and mode `0644` on `keycloak/start-reference.sh`
and `keycloak/nginx.conf`. These three public files are mounted into services
that run with their own user IDs.

```sh
umask 077
export KEYCLOAK_CONFIG_DIR="$HOME/.config/hosting-keycloak"
export KEYCLOAK_PUBLIC_ORIGIN=https://identity.example.net:9443
export KEYCLOAK_ADMIN_ORIGIN=https://identity.example.net:9444
mkdir -m 0700 "$KEYCLOAK_CONFIG_DIR"
bash keycloak/render-realm.sh https://mcp.example.net/mcp example-hosting \
  http://127.0.0.1:19876/mcp/oauth/callback > "$KEYCLOAK_CONFIG_DIR/realm.json"
openssl rand -hex 32 | tr -d '\n' > "$KEYCLOAK_CONFIG_DIR/database-password"
openssl rand -hex 32 | tr -d '\n' > "$KEYCLOAK_CONFIG_DIR/postgres-admin-password"
openssl rand -hex 32 | tr -d '\n' > "$KEYCLOAK_CONFIG_DIR/bootstrap-password"
```

Place the operator-supplied certificate chain in `tls.crt` and its private key
in `tls.key` under the private directory. With ordinary Docker Engine bind
mounts, Keycloak and PostgreSQL run as different unprivileged container UIDs.
Keep the enclosing host directory at mode `0700` and make only the mounted
realm and password leaves readable to those UIDs. For example, use
`0444` on `realm.json`, `database-password`, `postgres-admin-password` and
`bootstrap-password`.
Keep the TLS key at `0600`. The initial secret files have no trailing line
break and remain inside the private directory. Inspect permissions without
printing file contents.

PostgreSQL initializes a `keycloak` login that owns its dedicated database
without server administration privileges. Only PostgreSQL receives the
separate bootstrap PostgreSQL password. Initialization applies to an empty
database volume. To migrate an existing database, plan a backup and restore
into a separately initialized database with the least-privilege owner.

Install the realm with the temporary bootstrap admin overlay.

```sh
docker compose --project-name hosting-id --env-file /dev/null \
  -f keycloak/compose.yaml -f keycloak/bootstrap.compose.yaml up -d
```

Keycloak imports the realm only on initial creation. Use the existing realm
procedure below to add clients later. Back up PostgreSQL and manage TLS
renewals independently. If ports change, update both the matching origin
and `KEYCLOAK_LOGIN_PORT` or `KEYCLOAK_ADMIN_PORT`.

## Delegate account provisioning

Use the temporary master admin only for bootstrap. The public proxy denies
master, realm administration and dynamic registration endpoints. If you use
the private admin console, first set the master realm's frontend URL to the
private admin origin so its login also stays private. The bootstrap user can
do this through the private Admin REST API. The following example uses the
built-in master `admin-cli` only for this one-time private step.

```sh
private="$KEYCLOAK_CONFIG_DIR"
curl --disable --silent --fail --proto '=https' --noproxy '*' \
  --data 'grant_type=password&client_id=admin-cli&username=temporary-admin' \
  --data-urlencode "password@$private/bootstrap-password" \
  "$KEYCLOAK_ADMIN_ORIGIN/realms/master/protocol/openid-connect/token" \
  -o "$private/bootstrap-token-response.json"
jq -er '"Authorization: Bearer " + .access_token' \
  "$private/bootstrap-token-response.json" > "$private/bootstrap.header"
jq -n --arg origin "$KEYCLOAK_ADMIN_ORIGIN" '{attributes:{frontendUrl:$origin}}' \
  > "$private/master-update.json"
curl --disable --silent --fail --proto '=https' --noproxy '*' -X PUT \
  -H "@$private/bootstrap.header" -H 'Content-Type: application/json' \
  --data-binary "@$private/master-update.json" -o /dev/null \
  "$KEYCLOAK_ADMIN_ORIGIN/admin/realms/master"
```

On the private console create a durable administrator for recovery. In the
`hosting` realm, create a separate confidential client for provisioning, enable
only service accounts, and assign its service account the `realm-management`
`manage-users` role. Limit its role scope mappings to the same role and keep
the standard role mapper for this administrative client. Where fine-grained
admin permissions are configured, grant the required view and manage user
operations on the intended population. Keep credential access restricted to
operators. Verify the recovery account works, then remove the temporary master
admin and its local files. Restart Keycloak using only `keycloak/compose.yaml`
to remove the bootstrap secret mount.

Use a short-lived client-credentials token from the private
`/realms/hosting/protocol/openid-connect/token` endpoint to create or look up
users. Send the provisioning client's secret using curl's
`--data-urlencode "client_secret@/path/to/private/file"` form, with redirects
disabled. Store the token response and extract its raw `access_token` into a
mode-`0600` file. The generated MCP clients use authorization code with
S256 PKCE. Each client has its own registration and exact callback list.

## Create an account and grant access

Use the private operator prompt or pass a protected password-file path as
the last argument. Set `KEYCLOAK_CA_FILE` when the local CA is outside the
workstation trust store.

```sh
private="$KEYCLOAK_CONFIG_DIR"
bash keycloak/enroll-user.sh create example-user "$private/admin-token" \
  "$private/identity.json"

jq '. + {operation:"enroll",id:"example-operator",grants:[
  {project:"example",lane:"primary",actions:["start","read"]}
]}' "$private/identity.json" > "$private/enrollment.json"
bash enrollment/enroll.sh docker "$HOSTING_CONFIG_DIR" "$private/enrollment.json"
```

Creation alone gives no Hosting access. If the handoff is interrupted,
confirm the account and stable user ID in the private administration view.
Run `enroll-user.sh lookup` with the verified ID in place of the username
and use a fresh handoff file. A creation conflict leaves the existing
account and password intact. Use the same procedure for additional users.
For offboarding, revoke Hosting grants using the exact saved identity,
then disable the account and terminate its sessions in Keycloak. Retain the
Hosting principal binding. A recreated user has a new subject.

Configure both Hosting authentication sections with
`https://identity.example.net:9443/realms/hosting`, the same HTTPS `/mcp`
resource audience used to render the realm, and scope `sessions`. The
broker and edge need network access to the HTTPS discovery and JWKS
endpoints. The public listener serves both OIDC and RFC 8414 metadata for the
`hosting` realm. See the [operator enrollment](../enrollment/README.md) and
[Keycloak hostname](https://www.keycloak.org/server/hostname) guides.

## Register multiple static clients

Copy `keycloak/clients.example.json` to the private directory. Replace
`resource` with the same complete HTTPS `/mcp` URL configured in both
Hosting authentication sections. For each entry give a unique `id`, a
`type`, and a nonempty `redirectUris` list, with each URI written exactly as
the client sends it. The example uses illustrative client IDs and callbacks.

- `desktop` uses public OAuth with explicit `http://127.0.0.1:PORT/path`
  callbacks.
- `chat-public` uses public OAuth with HTTPS callbacks.
- `chat-confidential` uses HTTPS callbacks and authenticates the client at
  the token endpoint. Keycloak issues its credential after registration.

Use letters, digits, dots, underscores or hyphens in IDs (1 to 64 characters,
beginning with a letter or digit). Each callback needs an explicit absolute
URI, with no userinfo, fragments, or wildcards. The manifest has no credential
fields. Keep the configured manifest and all generated payloads private.

For a new realm, render `realm.json` before running the initial import.

```sh
umask 077
private="$KEYCLOAK_CONFIG_DIR"
bash keycloak/render-realm.sh --manifest "$private/clients.json" > "$private/realm.json"
```

### Add clients to an existing realm

Use the private Keycloak Admin Console with a realm administrator authorized
to manage clients. In the `hosting` realm, verify that the optional
`sessions` client scope has the Hosting resource audience mapper configured
with the exact same MCP URL. For a compatible realm that lacks this scope,
use `--manifest` to inspect the `sessions` entry under `clientScopes` and
create that scope and mapper privately before proceeding. If the existing
scope differs, review the effect on other clients before changing it.

```sh
umask 077
private="$KEYCLOAK_CONFIG_DIR"
bash keycloak/render-realm.sh --clients "$private/clients.json" > "$private/static-clients.json"
jq -e '.[] | select(.clientId == "example-chat-one")' "$private/static-clients.json" \
  > "$private/new-client.json"
```

Substitute the private client ID in the selector and repeat per registration.
For an unused ID, use **Clients → Import client** with the corresponding
JSON object. For an existing ID, review the changes against the current
registration and apply them in the console while preserving credentials and
unrelated settings. Verify the callback list, standard flow with S256 only,
subject mapper, and scope assignments. Under **Client scopes**, ensure there
are no inherited default scopes on the MCP client and that `sessions` is
assigned as optional. Retrieve any confidential client credential privately
from **Credentials** and enter it in the consumer's OAuth configuration.
The user still needs a Hosting enrollment and grants. See the
[Keycloak server administration guide](https://www.keycloak.org/docs/latest/server_admin/).

## OpenCode static registration

The samples use the exact callback shown above. Change the realm's static
registration and client configuration together when choosing another callback.

OpenCode v1 uses the server alias directly under `mcp`.

```json
{
  "mcp": {
    "hosting": {
      "type": "remote",
      "url": "https://mcp.example.net/mcp",
      "oauth": {
        "clientId": "example-hosting",
        "redirectUri": "http://127.0.0.1:19876/mcp/oauth/callback",
        "scope": "openid sessions"
      }
    }
  }
}
```

OpenCode v2 groups server aliases under `mcp.servers` and uses snake case.
See the [OpenCode migration guide](https://opencode.ai/docs/migrate-v1/).

```json
{
  "mcp": {
    "servers": {
      "hosting": {
        "type": "remote",
        "url": "https://mcp.example.net/mcp",
        "oauth": {
          "client_id": "example-hosting",
          "redirect_uri": "http://127.0.0.1:19876/mcp/oauth/callback",
          "scope": "openid sessions"
        }
      }
    }
  }
}
```

If OpenCode attempts dynamic registration and receives 404, check that it is
reading the static client ID at the location for your version. If the redirect
is rejected, compare the callback scheme, host, port and path to the realm's
registration. See the [OpenCode MCP documentation](https://opencode.ai/docs/mcp-servers).

## ChatGPT static registration

In ChatGPT Plugins, add a custom MCP server using the complete HTTPS MCP URL
and OAuth with your static client credentials. Copy the exact redirect URI
from that specific plugin's management page into the private manifest.
Use the URI displayed for that plugin and callback mode. Once registered,
complete installation and OAuth sign-in in the intended account or workspace
and check that an authorized tool works. See [OpenAI Plugin authentication](https://developers.openai.com/plugins/build/auth)
and [Add custom MCP server](https://developers.openai.com/api/docs/guides/custom-mcp-server).

## Claude static registration

Add a custom web connector with the complete HTTPS MCP URL. Under OAuth
client, select **Use your own OAuth client**. Enter its registered client ID
and a secret if Keycloak issued one. Claude documents
`https://claude.ai/api/mcp/auth_callback` as the redirect URI for this flow.
Connect in the intended account or workspace and check that an authorized
tool works. See the [Claude custom connector guide](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp).

The release fixture verifies local OAuth exchanges against disposable Keycloak
users. ChatGPT and Claude acceptance requires the corresponding accounts,
registrations and sign-in tests.
