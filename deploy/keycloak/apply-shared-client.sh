#!/usr/bin/env bash
set +x
set -euo pipefail
umask 077

fail() { printf '%s\n' "$1" >&2; exit 1; }

[[ $# == 2 ]] || fail 'Usage: bash apply-shared-client.sh PRIVATE_MANIFEST ADMIN_TOKEN_FILE'
admin=${KEYCLOAK_ADMIN_ORIGIN:-}
public=${KEYCLOAK_PUBLIC_ORIGIN:-}
origin='^https://[a-zA-Z0-9.-]+(:[1-9][0-9]{0,4})?$'
[[ "$admin" =~ $origin && "$public" =~ $origin && "$admin" != "$public" ]] ||
    fail 'Supply distinct HTTPS login and private admin origins.'

private_file() {
    local absolute mode links size
    absolute=$(realpath -ms -- "$1") || return 1
    [[ -f "$absolute" && -O "$absolute" && "$(realpath -e -- "$1")" == "$absolute" ]] || return 1
    read -r mode links size < <(stat -c '%a %h %s' -- "$absolute") || return 1
    (( (8#$mode & 077) == 0 && links == 1 && size > 0 && size <= 65536 ))
}

if ! private_file "$1" || ! private_file "$2"; then
    fail 'Use owned, protected, unlinked manifest and admin token files.'
fi
token=$(< "$2")
[[ "$token" =~ ^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$ ]] ||
    fail 'Provide a short-lived realm admin access token.'

work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
printf 'Authorization: Bearer %s\n' "$token" > "$work/header"
unset token
cp -- "$1" "$work/manifest"
jq -e '.clients | length==1 and .[0].type=="shared-public"' "$work/manifest" >/dev/null 2>&1 ||
    fail 'Supply one shared-public client in the manifest.'
directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
bash "$directory/render-realm.sh" --manifest "$work/manifest" > "$work/realm"
jq ' .clients[0] ' "$work/realm" > "$work/desired"
client=$(jq -er '.clientId' "$work/desired")
resource=$(jq -er '.clientScopes[0].protocolMappers[0].config["included.custom.audience"]' "$work/realm")

base="$admin/admin/realms/hosting"
opts=(--silent --proto '=https' --noproxy '*' --connect-timeout 5 --max-time 20
    -H "@$work/header" -o "$work/response" -w '%{http_code}')
if [[ -n "${KEYCLOAK_CA_FILE:-}" ]]; then opts+=(--cacert "$KEYCLOAK_CA_FILE"); fi
request() { curl --disable "${opts[@]}" "$@"; }
expect() {
    local code=$1 received
    shift
    if ! received=$(request "$@") || [[ "$received" != "$code" ]]; then
        fail 'The private admin request failed. Check access and retry with the same manifest.'
    fi
}
validate_id() { [[ "$1" =~ ^[[:alnum:]_-]{1,128}$ ]]; }

# Verify the realm's exact scope binding before granting the scope to a client.
expect 200 "$base/client-scopes"
scope=$(jq -er '[.[] | select(.name=="sessions")] | select(length==1) | .[0].id' "$work/response") ||
    fail 'The realm needs one sessions client scope.'
validate_id "$scope" || fail 'The scope identifier was invalid.'
expect 200 "$base/client-scopes/$scope"
jq -e --arg resource "$resource" '
    .name=="sessions" and .protocol=="openid-connect" and
    .attributes["include.in.token.scope"]=="true" and
    (.protocolMappers | length==1 and (.[0] |
        .protocol=="openid-connect" and .protocolMapper=="oidc-audience-mapper" and
        .config["included.custom.audience"]==$resource and .config["access.token.claim"]=="true" and
        .config["id.token.claim"]=="false" and (.config["included.client.audience"] // "")==""))
' "$work/response" >/dev/null || fail 'The existing sessions scope does not match the manifest resource.'

find_client() {
    expect 200 "$base/clients?clientId=$client"
    id=$(jq -er --arg client "$client" '
        [.[] | select(.clientId==$client)] |
        if length==0 then "" elif length==1 then .[0].id else empty end
    ' "$work/response") || fail 'Could not identify a unique client.'
    [[ -z "$id" ]] || validate_id "$id" || fail 'The client identifier was invalid.'
}

# Existing registrations may only be adopted when they have the shared profile's
# security settings and exact scope grants. Only their callbacks are reconciled.
read_compatible_client() {
    expect 200 "$base/clients/$id"
    jq -e --arg client "$client" '
        .clientId==$client and .enabled==true and .protocol=="openid-connect" and
        .publicClient==true and .standardFlowEnabled==true and .directAccessGrantsEnabled==false and
        .implicitFlowEnabled==false and .serviceAccountsEnabled==false and .fullScopeAllowed==false and
        .attributes["pkce.code.challenge.method"]=="S256" and
        .attributes["exclude.issuer.from.auth.response"]!="true" and
        (.webOrigins // [] | length==0) and
        (.protocolMappers | length==1 and (.[0] |
            .protocol=="openid-connect" and .protocolMapper=="oidc-sub-mapper" and
            .config["access.token.claim"]=="true"))
    ' "$work/response" >/dev/null || fail 'The existing client conflicts with the shared OAuth profile.'
    cp -- "$work/response" "$work/current"
    expect 200 "$base/clients/$id/default-client-scopes"
    jq -e --arg scope "$scope" 'length==1 and .[0].id==$scope' "$work/response" >/dev/null ||
        fail 'The existing client has unexpected default scopes.'
    expect 200 "$base/clients/$id/optional-client-scopes"
    jq -e 'type=="array" and length==0' "$work/response" >/dev/null ||
        fail 'The existing client has unexpected optional scopes.'
}

callbacks_match() {
    jq -e --slurpfile desired "$work/desired" '
        (.redirectUris | sort) == ($desired[0].redirectUris | sort)
    ' "$work/current" >/dev/null
}

find_client
if [[ -z "$id" ]]; then
    expect 201 -H 'Content-Type: application/json' --data-binary "@$work/desired" "$base/clients"
    find_client
    [[ -n "$id" ]] || fail 'The created client was not visible. Reapply the same manifest to reconcile it.'
    result=registered
else
    result=unchanged
fi
read_compatible_client
if ! callbacks_match; then
    jq --slurpfile desired "$work/desired" '.redirectUris=$desired[0].redirectUris' "$work/current" > "$work/update"
    expect 204 -X PUT -H 'Content-Type: application/json' --data-binary "@$work/update" "$base/clients/$id"
    read_compatible_client
    callbacks_match || fail 'The callback update could not be verified.'
    result=updated
fi
printf 'Shared OAuth client %s.\n' "$result"
