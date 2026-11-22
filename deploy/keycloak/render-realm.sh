#!/usr/bin/env bash
set -euo pipefail
umask 077

[[ $# -ge 2 && $# -le 3 ]] || {
    printf '%s\n' 'Usage: bash render-realm.sh HTTPS_MCP_RESOURCE CLIENT_ID [LOOPBACK_CALLBACK]' >&2
    exit 2
}
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

jq -n --arg audience "$resource" --arg client "$client" --arg redirect "$callback" '{
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
  clients: [{
    clientId: $client,
    enabled: true,
    publicClient: true,
    protocol: "openid-connect",
    redirectUris: [$redirect],
    standardFlowEnabled: true,
    directAccessGrantsEnabled: false,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: false,
    fullScopeAllowed: false,
    defaultClientScopes: ["basic"],
    optionalClientScopes: ["sessions"],
    attributes: {"pkce.code.challenge.method":"S256"}
  }],
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
}'
