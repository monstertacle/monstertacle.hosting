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

Keycloak imports the realm only on initial creation. Apply further changes
as private administrative operations. Back up PostgreSQL and manage TLS
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
mode-`0600` file. The public MCP client has authorization code with S256 PKCE,
no client secret, and a single registered callback.

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
