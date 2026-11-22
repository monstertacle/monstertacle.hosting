#!/usr/bin/env bash
set -euo pipefail
umask 077

usage() {
    printf '%s\n' 'Usage: bash render-realm.sh HTTPS_MCP_RESOURCE CLIENT_ID [LOOPBACK_CALLBACK]' \
        '       bash render-realm.sh --manifest MANIFEST.json' \
        '       bash render-realm.sh --clients MANIFEST.json' >&2
    exit 2
}

format=realm
case "${1:-}" in
    --manifest|--clients)
        [[ $# == 2 && -f "$2" && -r "$2" ]] || usage
        if [[ "$1" == --clients ]]; then format=clients; fi
        if ! manifest=$(jq -ces '
          def port: . == null or (tonumber <= 65535);
          def origin:
            "https://[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*(?::(?<port>[1-9][0-9]{0,4}))?";
          def matches($pattern):
            type == "string" and any(capture("\\A" + $pattern + "\\z"); .port | port);
          def loopback: matches("http://127\\.0\\.0\\.1:(?<port>[1-9][0-9]{0,4})/[A-Za-z0-9/_-]+");
          def https_callback:
            "(?:[A-Za-z0-9._~!$&()+,;=:@/-]|%[A-Fa-f0-9]{2})" as $path
            | matches(origin + "(?:/" + $path + "*)?(?:\\?(?:" + $path + "|\\?)*)?")
                and (test("%(?:2a|5c|0[0-9a-f]|1[0-9a-f]|7f)"; "i") | not);
          def client:
            type == "object" and keys == ["id","redirectUris","type"]
            and (.id | type == "string" and test("\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z"))
            and (.redirectUris | type == "array" and length > 0 and length == (unique | length))
            and (if .type == "desktop" then all(.redirectUris[]; loopback)
                 elif .type == "chat-public" or .type == "chat-confidential" then
                     all(.redirectUris[]; https_callback)
                 else false end);
          select(length == 1) | .[0]
          | select(type == "object" and keys == ["clients","resource"]
              and (.resource | matches(origin + "/mcp"))
              and (.clients | type == "array" and length > 0 and all(.[]; client)
                   and length == (map(.id) | unique | length)))
        ' < "$2" 2>/dev/null); then
            printf '%s\n' 'Invalid static client manifest. See the manifest example and schema in the Keycloak guide.' >&2
            exit 2
        fi
        ;;
    *)
        [[ $# -ge 2 && $# -le 3 ]] || usage
        resource=$1
        client=$2
        callback=${3:-http://127.0.0.1:19876/mcp/oauth/callback}
        if [[ ! "$resource" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/mcp$ ||
              ! "$client" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ||
              ! "$callback" =~ ^http://127\.0\.0\.1:([1-9][0-9]{0,4})/[A-Za-z0-9/_-]+$ ]] ||
            (( BASH_REMATCH[1] > 65535 )); then
            printf '%s\n' 'Supply an exact HTTPS /mcp resource, static client ID and IPv4 loopback callback with port.' >&2
            exit 2
        fi
        manifest=$(jq -cn --arg resource "$resource" --arg id "$client" --arg callback "$callback" \
            '{$resource, clients: [{$id, type:"desktop", redirectUris:[$callback]}]}')
        ;;
esac

# Read the private manifest from standard input, keeping it out of process arguments.
jq --arg format "$format" '
  def static_client: {
    clientId: .id,
    enabled: true,
    publicClient: (.type != "chat-confidential"),
    protocol: "openid-connect",
    redirectUris: .redirectUris,
    standardFlowEnabled: true,
    directAccessGrantsEnabled: false,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: false,
    fullScopeAllowed: false,
    defaultClientScopes: [],
    optionalClientScopes: ["sessions"],
    attributes: {"pkce.code.challenge.method":"S256"},
    protocolMappers: [{
      name: "Stable local user subject",
      protocol: "openid-connect",
      protocolMapper: "oidc-sub-mapper",
      config: {"access.token.claim":"true", "introspection.token.claim":"true"}
    }]
  } + if .type == "chat-confidential" then {clientAuthenticatorType: "client-secret"} else {} end;
  (.clients | map(static_client)) as $clients
  | .resource as $audience
  | if $format == "clients" then $clients else {
  realm: "hosting",
  enabled: true,
  sslRequired: "all",
  registrationAllowed: false,
  loginWithEmailAllowed: false,
  resetPasswordAllowed: false,
  bruteForceProtected: true,
  eventsListeners: [],
  requiredActions: [{
    alias: "VERIFY_PROFILE",
    name: "Verify Profile",
    providerId: "VERIFY_PROFILE",
    enabled: false,
    defaultAction: false,
    priority: 90,
    config: {}
  }],
  accessTokenLifespan: 300,
  clients: $clients,
  clientScopes: [{
    name: "sessions",
    protocol: "openid-connect",
    attributes: {"include.in.token.scope":"true"},
    protocolMappers: [{
      name: "Hosting resource audience",
      protocol: "openid-connect",
      protocolMapper: "oidc-audience-mapper",
      config: {
        "included.custom.audience": $audience,
        "access.token.claim": "true",
        "id.token.claim": "false",
        "introspection.token.claim": "true"
      }
    }]
  }]
} end' <<< "$manifest"
