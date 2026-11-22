#!/usr/bin/env bash
# Sourced by quickstart.sh.
# shellcheck disable=SC2154

base64url() {
    openssl base64 -A | tr '+/' '-_' | tr -d '='
}

signed_header() {
    local subject=$1 audience=$2 path=$3 token_issuer=${4:-$issuer} now payload unsigned signature
    now=$(date +%s)
    payload=$(jq -cn --arg iss "$token_issuer" --arg aud "$audience" --arg sub "$subject" \
        --argjson now "$now" '{iss:$iss,aud:$aud,sub:$sub,scope:"sessions",iat:$now,nbf:($now-5),exp:($now+300)}')
    unsigned="$(printf '{"alg":"RS256","kid":"local-fixture","typ":"JWT"}' | base64url).$(printf '%s' "$payload" | base64url)"
    signature=$(printf '%s' "$unsigned" | openssl dgst -sha256 -sign "$scratch/signing.key" | base64url)
    printf 'Authorization: Bearer %s.%s\n' "$unsigned" "$signature" > "$path"
}

make_fixture() {
    local role lane hex modulus
    mkdir -m 0755 "$HOSTING_CONFIG_DIR"/{broker,edge,issuer,worker,worker-secondary}
    certificate broker broker serverAuth
    certificate edge-server localhost serverAuth
    certificate edge-client fixture-edge clientAuth
    certificate worker-primary fixture-primary clientAuth
    certificate worker-secondary fixture-secondary clientAuth
    certificate issuer issuer serverAuth

    # The enclosing config directory is 0700. Each unprivileged service
    # receives only its own files in a read-only mount.
    install -m 0444 "$scratch/broker.pfx" "$HOSTING_CONFIG_DIR/broker/server.pfx"
    install -m 0444 "$scratch/edge-server.pfx" "$HOSTING_CONFIG_DIR/edge/server.pfx"
    install -m 0444 "$scratch/edge-client.pfx" "$HOSTING_CONFIG_DIR/edge/client.pfx"
    install -m 0444 "$scratch/issuer.pfx" "$HOSTING_CONFIG_DIR/issuer/server.pfx"
    cat /etc/ssl/certs/ca-certificates.crt "$scratch/broker.pem" "$scratch/issuer.pem" > "$scratch/ca.pem"
    if [[ "$enrollment" == keycloak ]]; then cat "$KEYCLOAK_CA_FILE" >> "$scratch/ca.pem"; fi

    jq -cn --arg issuer "$issuer" --arg subject "$subject" '
        {version:1,projects:[{id:"fixture",lanes:["primary","secondary"]}],people:[
            {id:"fixture-operator",identities:[{issuer:$issuer,subject:$subject}],
             grants:[("primary","secondary") | {project:"fixture",lane:.,actions:["start","read","result","artifact"]}]}]}
    ' > "$scratch/policy.json"
    for role in broker edge; do
        install -m 0444 "$scratch/policy.json" "$HOSTING_CONFIG_DIR/$role/policy.json"
        install -m 0444 "$scratch/ca.pem" "$HOSTING_CONFIG_DIR/$role/ca.pem"
    done

    cat > "$HOSTING_CONFIG_DIR/broker.env" <<EOF_ENV
Broker__Authentication__Issuer=$issuer
Broker__Authentication__Audience=$resource
Broker__Authentication__Scope=sessions
Broker__Authentication__EdgeCertificateSha256=$(fingerprint edge-client)
Broker__Workers__fixture-primary__Lane=primary
Broker__Workers__fixture-primary__CertificateSha256=$(fingerprint worker-primary)
Broker__Workers__fixture-secondary__Lane=secondary
Broker__Workers__fixture-secondary__CertificateSha256=$(fingerprint worker-secondary)
SSL_CERT_FILE=/run/hosting/ca.pem
Logging__LogLevel__Default=Critical
EOF_ENV
    cat > "$HOSTING_CONFIG_DIR/edge.env" <<EOF_ENV
Mcp__Authentication__Issuer=$issuer
Mcp__Authentication__Audience=$resource
Mcp__Authentication__Scope=sessions
SSL_CERT_FILE=/run/hosting/ca.pem
Logging__LogLevel__Default=Critical
EOF_ENV
    for lane in primary secondary; do
        role=worker
        if [[ "$lane" == secondary ]]; then role=worker-secondary; fi
        install -m 0444 "$scratch/worker-$lane.pfx" "$HOSTING_CONFIG_DIR/$role/client.pfx"
        install -m 0444 "$scratch/ca.pem" "$HOSTING_CONFIG_DIR/$role/ca.pem"
        cat > "$HOSTING_CONFIG_DIR/$role.env" <<EOF_ENV
Worker__Profile=deterministic-$lane
Worker__TrustScope=$(cat /proc/sys/kernel/random/uuid)
SSL_CERT_FILE=/run/hosting/ca.pem
Logging__LogLevel__Default=Critical
EOF_ENV
    done

    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -pkeyopt rsa_keygen_pubexp:65537 \
        -out "$scratch/signing.key" 2>/dev/null
    hex=$(openssl rsa -in "$scratch/signing.key" -noout -modulus | cut -d = -f 2)
    [[ "$hex" =~ ^[0-9A-F]{512}$ ]]
    # Convert each pair of hex digits to a byte before base64url encoding.
    # shellcheck disable=SC2001
    modulus=$(printf '%b' "$(sed 's/../\\x&/g' <<< "$hex")" | base64url)
    jq -cn --arg n "$modulus" '{keys:[{kty:"RSA",kid:"local-fixture",use:"sig",alg:"RS256",n:$n,e:"AQAB"}]}' > "$scratch/jwks"
    jq -cn --arg issuer "$issuer" '{issuer:$issuer,jwks_uri:($issuer+"/jwks")}' > "$scratch/metadata"
    install -m 0444 "$scratch/"{jwks,metadata} "$HOSTING_CONFIG_DIR/issuer/"

    signed_header "$subject" "$resource" "$scratch/granted.header"
    signed_header unlisted "$resource" "$scratch/unlisted.header"
    signed_header "$subject" "$resource/wrong-audience" "$scratch/wrong-audience.header"
    signed_header "$subject" "$resource" "$scratch/wrong-issuer.header" "$issuer/wrong"
    rm -- "$scratch/signing.key"
}
