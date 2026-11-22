#!/usr/bin/env bash
# Sourced by quickstart.sh. All tokens, passwords and browser state are disposable.
# shellcheck disable=SC2034,SC2154

keycloak_request() {
    curl --disable --silent --noproxy '*' --proto '=https' --connect-timeout 3 --max-time 15 \
        --cacert "$KEYCLOAK_CA_FILE" --resolve identity.example.test:9443:127.0.0.1 "$@"
}

keycloak_startup_diagnostics() {
    local status=$1
    printf 'Keycloak discovery returned HTTP %s.\n' "${status:-unavailable}" >&3
    "${keycloak_compose[@]}" ps --all --format json 2>/dev/null |
        jq -rs 'flatten | .[] | select(.Service | IN("proxy","keycloak","database"))
            | "\(.Service) \(.State) \(.Health // "")"' >&3 || true
    "${keycloak_compose[@]}" logs --no-color --tail 60 > "$scratch/startup-diagnostics" 2>&1 || true
    if [[ -f "$scratch/keycloak-up.log" ]]; then
        cat -- "$scratch/keycloak-up.log" >> "$scratch/startup-diagnostics"
    fi
    if grep -Eqi 'toomanyrequests|pull access denied|unauthorized|manifest unknown|failed to pull' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'An upstream image could not be pulled.' >&3
    fi
    if grep -Eqi 'address already in use|port is already allocated' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'A loopback fixture port is already in use.' >&3
    fi
    if grep -Eqi 'permission denied|not readable|cannot load certificate' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'A mounted Keycloak reference file could not be accessed.' >&3
    fi
    if grep -Eqi 'password authentication failed|failed to obtain JDBC|database .* does not exist' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'Keycloak could not authenticate to its database.' >&3
    fi
    if grep -Eqi 'host not found in upstream|could not resolve host' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'The proxy could not resolve its private upstream.' >&3
    fi
    if grep -Eqi 'unknown directive|invalid number of arguments|pcre.*failed' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'The proxy rejected its configuration.' >&3
    fi
    if grep -Eqi 'failed to start|initialization error|failed to import|failed to migrate' "$scratch/startup-diagnostics"; then
        printf '%s\n' 'Keycloak reported a startup, import or migration failure.' >&3
    fi
}

check_keycloak_renderer() {
    local example="$bundle/keycloak/clients.example.json"
    local renderer="$bundle/keycloak/render-realm.sh"
    local check="$scratch/render-check"
    local filter
    mkdir -m 0700 "$check"
    bash "$renderer" --manifest "$example" > "$check/realm.json"
    bash "$renderer" --manifest "$example" > "$check/repeat.json"
    cmp "$check/realm.json" "$check/repeat.json"
    bash "$renderer" --clients "$example" > "$check/clients.json"
    jq -e --slurpfile clients "$check/clients.json" '
        .clients == $clients[0] and (.clients | length == 3)
        and (.clients | map(.publicClient) == [true,false,true])
        and all(.clients[]; .attributes["pkce.code.challenge.method"] == "S256"
            and .standardFlowEnabled and (.implicitFlowEnabled | not)
            and (.directAccessGrantsEnabled | not) and (.serviceAccountsEnabled | not)
            and (.fullScopeAllowed | not) and .defaultClientScopes == []
            and .optionalClientScopes == ["sessions"] and (has("secret") | not)
            and any(.protocolMappers[]; .protocolMapper == "oidc-sub-mapper"))
        and .clientScopes[0].protocolMappers[0].config["included.custom.audience"] == "https://mcp.example.net/mcp"
    ' "$check/realm.json" >/dev/null
    bash "$renderer" https://mcp.example.net/mcp example-hosting > "$check/legacy.json"
    jq -e --slurpfile realm "$check/realm.json" '
        .clients[0].redirectUris == ["http://127.0.0.1:19876/mcp/oauth/callback"]
        and .clients == $realm[0].clients[:1] and .clientScopes == $realm[0].clientScopes
    ' "$check/legacy.json" >/dev/null
    for filter in '.clients += [.clients[0]]' '.clients = []' '.clients[0].id = "../desktop"' \
        '.clients[0].redirectUris = ["http://127.0.0.1:65536/callback"]' \
        '.clients[0].redirectUris = ["http://localhost:19876/callback"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/*"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/%2A"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/%5Ccallback"]' \
        '.clients[1].redirectUris = ["https://user@chat.example.net/callback"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/callback#fragment"]' \
        '.clients[1].redirectUris = ["https://chat.example.net:99999/callback"]' \
        '.clients[1].redirectUris = ["http://chat.example.net/callback"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/%QQ"]' \
        '.clients[1].redirectUris = ["https://chat.example.net/callback\n"]' \
        '.clients[1].redirectUris = "https://chat.example.net/callback"' \
        '.clients[1].redirectUris += [.clients[1].redirectUris[0]]' \
        '.clients[1].secret = null' '.clients[1].type = "unknown"' \
        '.resource = "https://mcp.example.net/mcp?extra=1"' '., .'; do
        jq "$filter" "$example" > "$check/invalid.json"
        if bash "$renderer" --manifest "$check/invalid.json" > "$check/rejected.json" 2>/dev/null; then
            return 1
        fi
        [[ ! -s "$check/rejected.json" ]] || return 1
    done
    printf '%s\n' '{' > "$check/invalid.json"
    if bash "$renderer" --manifest "$check/invalid.json" > "$check/rejected.json" 2>/dev/null; then
        return 1
    fi
    [[ ! -s "$check/rejected.json" ]]
}

start_keycloak_fixture() {
    local attempt status path user chat_callback chat_id
    export KEYCLOAK_CONFIG_DIR="$scratch/keycloak"
    export KEYCLOAK_PUBLIC_ORIGIN=https://identity.example.test:9443
    export KEYCLOAK_ADMIN_ORIGIN=https://localhost:9444
    export KEYCLOAK_LOGIN_PORT=9443 KEYCLOAK_ADMIN_PORT=9444
    export KEYCLOAK_CA_FILE="$KEYCLOAK_CONFIG_DIR/tls.crt"
    issuer="$KEYCLOAK_PUBLIC_ORIGIN/realms/hosting"
    mkdir -m 0700 "$KEYCLOAK_CONFIG_DIR"
    openssl req -x509 -newkey rsa:2048 -noenc -days 1 -subj '/CN=identity.example.test' \
        -addext 'subjectAltName=DNS:identity.example.test,DNS:localhost' \
        -keyout "$KEYCLOAK_CONFIG_DIR/tls.key" -out "$KEYCLOAK_CA_FILE" >/dev/null 2>&1

    phase="Keycloak deterministic manifest rendering"
    check_keycloak_renderer
    jq --arg resource "$resource" '.resource = $resource' "$bundle/keycloak/clients.example.json" > "$scratch/clients.json"
    callback=$(jq -er '.clients[0].redirectUris[0]' "$scratch/clients.json")
    chat_callback=$(jq -er '.clients[1].redirectUris[0]' "$scratch/clients.json")
    bash "$bundle/keycloak/render-realm.sh" --manifest "$scratch/clients.json" |
        jq '.clients += [(.clients[0] | .clientId = "wrong-audience" | .protocolMappers += [
            {name:"Extra audience",protocol:"openid-connect",protocolMapper:"oidc-audience-mapper",
                config:{"included.custom.audience":"https://other.example.test/mcp","access.token.claim":"true"}}
        ])]' > "$KEYCLOAK_CONFIG_DIR/realm.json"
    for path in database-password postgres-admin-password bootstrap-password; do
        openssl rand -hex 24 | tr -d '\n' > "$KEYCLOAK_CONFIG_DIR/$path"
    done
    chmod 0444 "$KEYCLOAK_CONFIG_DIR/"{realm.json,database-password,postgres-admin-password,bootstrap-password}
    cat > "$scratch/keycloak-network.yaml" <<EOF_NETWORK
services:
  proxy:
    networks:
      frontend:
        aliases: [identity.example.test]
EOF_NETWORK
    cat > "$scratch/hosting-keycloak-network.yaml" <<EOF_NETWORK
services:
  broker:
    networks: [private, outbound, issuer-fixture]
  edge:
    networks: [private, outbound, issuer-fixture]
networks:
  issuer-fixture:
    external: true
    name: ${project}-id_frontend
EOF_NETWORK
    keycloak_compose=(docker compose --project-name "$project-id" --env-file /dev/null
        -f "$bundle/keycloak/compose.yaml" -f "$bundle/keycloak/bootstrap.compose.yaml" -f "$scratch/keycloak-network.yaml")
    keycloak_started=true
    if ! "${keycloak_compose[@]}" up --detach > "$scratch/keycloak-up.log" 2>&1; then
        keycloak_startup_diagnostics unavailable
        return 1
    fi

    phase="Keycloak discovery"
    for ((attempt=0; attempt<90; attempt++)); do
        status=$(keycloak_request -o "$scratch/discovery.json" -w '%{http_code}' \
            "$issuer/.well-known/openid-configuration" || true)
        [[ "$status" != 200 ]] || break
        sleep 1
    done
    if [[ "$status" != 200 ]]; then
        keycloak_startup_diagnostics "$status"
        return 1
    fi
    phase="Keycloak RFC 8414 discovery"
    keycloak_request --fail -o "$scratch/authorization-server.json" \
        "$KEYCLOAK_PUBLIC_ORIGIN/.well-known/oauth-authorization-server/realms/hosting"
    for path in discovery authorization-server; do
        jq -e --arg issuer "$issuer" '
            .issuer == $issuer and .authorization_endpoint == ($issuer + "/protocol/openid-connect/auth")
            and .token_endpoint == ($issuer + "/protocol/openid-connect/token")
        ' "$scratch/$path.json" >/dev/null
    done
    jq -e '.code_challenge_methods_supported | index("S256") != null' "$scratch/discovery.json" >/dev/null

    phase="Keycloak database ownership"
    [[ "$("${keycloak_compose[@]}" exec -T --user postgres database psql --no-psqlrc -U postgres -d postgres -Atc \
        "SELECT pg_get_userbyid(datdba) = 'keycloak' AND NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
         FROM pg_database JOIN pg_roles ON rolname = 'keycloak' WHERE datname = 'keycloak';")" == t ]]

    phase="Keycloak public listener isolation"
    for path in /admin/realms/hosting/users /realms/master/.well-known/openid-configuration \
        /.well-known/oauth-authorization-server/realms/master \
        /realms/hosting/clients-registrations/openid-connect /realms/hosting/login-actions/registration /health; do
        [[ "$(keycloak_request -o /dev/null -w '%{http_code}' "$KEYCLOAK_PUBLIC_ORIGIN$path")" == 404 ]]
    done
    [[ "$(keycloak_request -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
        --data '{}' "$issuer/clients-registrations/openid-connect")" == 404 ]]
    status=$(keycloak_request -o "$scratch/public-password.json" -w '%{http_code}' \
        --data 'grant_type=password&client_id=example-hosting' "$issuer/protocol/openid-connect/token")
    [[ "$status" == 400 ]]
    jq -e '.access_token == null and .error == "unauthorized_client"' "$scratch/public-password.json" >/dev/null

    phase="Keycloak account creation"
    keycloak_request --fail --data 'grant_type=password&client_id=admin-cli&username=temporary-admin' \
        --data-urlencode "password@$KEYCLOAK_CONFIG_DIR/bootstrap-password" \
        "$KEYCLOAK_ADMIN_ORIGIN/realms/master/protocol/openid-connect/token" -o "$scratch/admin-response.json"
    jq -er '.access_token' "$scratch/admin-response.json" > "$scratch/admin-token"
    for user in person-one person-two; do
        openssl rand -hex 24 | tr -d '\n' > "$scratch/$user.password"
        bash "$bundle/keycloak/enroll-user.sh" create "$user" "$scratch/admin-token" \
            "$scratch/$user.json" "$scratch/$user.password"
    done
    subject=$(jq -er '.subject' "$scratch/person-one.json")
    additional_subject=$(jq -er '.subject' "$scratch/person-two.json")
    [[ "$subject" != "$additional_subject" ]]
    bash "$bundle/keycloak/enroll-user.sh" lookup "$subject" "$scratch/admin-token" "$scratch/lookup.json"
    cmp "$scratch/person-one.json" "$scratch/lookup.json"

    phase="Keycloak authorization code and S256 PKCE"
    keycloak_code person-one example-hosting missing
    keycloak_code person-one example-hosting wrong
    keycloak_code person-one example-hosting granted
    keycloak_code person-two example-hosting unlisted
    keycloak_code person-one wrong-audience wrong-audience

    phase="Keycloak confidential client secret retrieval"
    printf 'Authorization: Bearer %s\n' "$(< "$scratch/admin-token")" > "$scratch/keycloak-admin.header"
    local admin_clients="$KEYCLOAK_ADMIN_ORIGIN/admin/realms/hosting/clients"
    keycloak_request --fail -H "@$scratch/keycloak-admin.header" \
        "$admin_clients?clientId=example-chat-one" -o "$scratch/chat-registration.json"
    chat_id=$(jq -er 'select(length == 1) | .[0] | select(.clientId == "example-chat-one"
        and .publicClient == false and .clientAuthenticatorType == "client-secret") | .id' \
        "$scratch/chat-registration.json")
    [[ "$chat_id" =~ ^[A-Za-z0-9-]+$ ]]
    keycloak_request --fail -H "@$scratch/keycloak-admin.header" \
        "$admin_clients/$chat_id/client-secret" -o "$scratch/chat-credential.json"
    jq -jer '.value | strings | select(length > 0)' "$scratch/chat-credential.json" > "$scratch/chat-secret"
    keycloak_code person-one example-chat-one wrong-redirect "$chat_callback-extra"
    keycloak_code person-one example-chat-one missing-secret "$chat_callback"
    keycloak_code person-one example-chat-one missing "$chat_callback" "$scratch/chat-secret"
    keycloak_code person-one example-chat-one confidential "$chat_callback" "$scratch/chat-secret"
    keycloak_code person-one example-chat-two chat-public "$(jq -er '.clients[2].redirectUris[0]' "$scratch/clients.json")"
    compose+=(-f "$scratch/hosting-keycloak-network.yaml")
}

keycloak_code() {
    local user=$1 client=$2 test_case=$3 redirect_uri=${4:-$callback} client_secret=${5:-}
    local directory status login_url query redirect encoded remainder
    local protocol="$issuer/protocol/openid-connect"
    directory=$(mktemp -d "$scratch/oauth.XXXXXXXX")
    openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n' > "$directory/verifier"
    openssl dgst -sha256 -binary "$directory/verifier" |
        openssl base64 -A | tr '+/' '-_' | tr -d '=' > "$directory/challenge"
    phase="Keycloak authorization request ($test_case)"
    status=$(keycloak_request -o "$directory/login.html" -D "$directory/auth-headers" -w '%{http_code}' -c "$directory/cookies" -G \
        --data-urlencode "client_id=$client" --data-urlencode "redirect_uri=$redirect_uri" \
        --data 'response_type=code' --data-urlencode 'scope=openid sessions' \
        --data-urlencode "code_challenge@$directory/challenge" --data 'code_challenge_method=S256' \
        "$protocol/auth")
    if [[ "$test_case" == wrong-redirect ]]; then
        [[ "$status" == 400 ]] && ! grep -qi '^location:' "$directory/auth-headers"
        return
    fi
    [[ "$status" == 200 ]]
    login_url=$(sed -nE 's/.*id="kc-form-login"[^>]*action="([^"]+)".*/\1/p' "$directory/login.html" | sed 's/\&amp;/\&/g')
    query=${login_url#"$issuer/login-actions/authenticate?"}
    [[ "$login_url" == "$issuer/login-actions/authenticate?"* && "$query" =~ ^[A-Za-z0-9._~%=\&+-]+$ ]]
    printf 'url = "%s"\n' "$login_url" > "$directory/login.curl"
    phase="Keycloak browser form submission ($test_case)"
    status=$(keycloak_request -D "$directory/headers" -o "$directory/login-result" -w '%{http_code}' \
        -b "$directory/cookies" --data-urlencode "username=$user" \
        --data-urlencode "password@$scratch/$user.password" --config "$directory/login.curl")
    [[ "$status" == 302 || "$status" == 303 ]]
    redirect=$(awk 'tolower($1) == "location:" {sub(/^[^:]+:[[:blank:]]*/, ""); sub(/\r$/, ""); print}' "$directory/headers")
    [[ "$redirect" == "$redirect_uri?"* && "$redirect" =~ [\?\&]code=([A-Za-z0-9._-]+)($|\&) ]]
    printf '%s' "${BASH_REMATCH[1]}" > "$directory/code"

    local -a verifier=() authentication=()
    if [[ -n "$client_secret" ]]; then
        authentication=(--data-urlencode "client_secret@$client_secret")
    fi
    case "$test_case" in
        missing) ;;
        wrong)
            openssl rand -hex 32 | tr -d '\n' > "$directory/wrong"
            verifier=(--data-urlencode "code_verifier@$directory/wrong")
            ;;
        *) verifier=(--data-urlencode "code_verifier@$directory/verifier") ;;
    esac
    phase="Keycloak authorization code redemption ($test_case)"
    status=$(keycloak_request -o "$directory/token.json" -w '%{http_code}' --data 'grant_type=authorization_code' \
        --data-urlencode "client_id=$client" --data-urlencode "redirect_uri=$redirect_uri" \
        --data-urlencode "code@$directory/code" "${verifier[@]}" "${authentication[@]}" "$protocol/token")
    if [[ "$test_case" == missing-secret ]]; then
        [[ "$status" == 400 || "$status" == 401 ]]
        jq -e '.access_token == null and .error == "invalid_client"' "$directory/token.json" >/dev/null
        return
    fi
    if [[ "$test_case" == missing || "$test_case" == wrong ]]; then
        [[ "$status" == 400 ]]
        jq -e '.access_token == null and (.error == "invalid_grant" or .error == "invalid_request")' "$directory/token.json" >/dev/null
        return
    fi
    [[ "$status" == 200 ]]
    phase="Keycloak access token claims ($test_case)"
    encoded=$(jq -er '.access_token | split(".")[1]' "$directory/token.json" | tr '_-' '/+')
    remainder=$(( ${#encoded} % 4 ))
    if (( remainder > 0 )); then printf -v encoded '%s%.*s' "$encoded" "$((4 - remainder))" '==='; fi
    printf '%s' "$encoded" | base64 --decode > "$directory/claims.json"
    jq -e --slurpfile identity "$scratch/$user.json" --arg audience "$resource" --arg mode "$test_case" '
        .iss == $identity[0].issuer and .sub == $identity[0].subject and
        (.scope | split(" ") | index("sessions") != null) and
        (if $mode == "wrong-audience" then (.aud | type == "array" and length > 1) else
            (.aud == $audience or .aud == [$audience]) end)
    ' "$directory/claims.json" >/dev/null
    jq -er '"Authorization: Bearer " + .access_token' "$directory/token.json" > "$scratch/keycloak-$test_case.header"
}
