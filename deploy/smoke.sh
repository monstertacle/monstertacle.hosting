#!/usr/bin/env bash
set -euo pipefail
umask 077

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/hosting-smoke.XXXXXXXX")
project="hosting-smoke-${scratch##*.}"
project=${project,,}
export HOSTING_CONFIG_DIR="$scratch/config"
export HOSTING_EDGE_PORT="${HOSTING_EDGE_PORT:-28443}"
compose=(docker compose --project-name "$project" --file "$repo/deploy/compose.yaml")
started=false
cleanup() {
    if [[ "$started" == true ]]; then
        "${compose[@]}" down --volumes --remove-orphans >&2
    fi
    find "$scratch" -depth -delete
}
trap cleanup EXIT

if [[ $# -gt 1 || ( $# -eq 1 && "$1" != "--config-only" ) ]]; then
    printf 'Usage: %s [--config-only]\n' "$0" >&2
    exit 2
fi

mkdir -p "$HOSTING_CONFIG_DIR"/{broker,worker,edge}
for name in broker worker edge; do
    touch "$HOSTING_CONFIG_DIR/$name.env"
done
"${compose[@]}" config --quiet
if [[ $# -eq 1 ]]; then
    exit 0
fi

certificate() {
    local name=$1 dns=$2 purpose=$3
    openssl req -x509 -newkey rsa:2048 -noenc -days 1 -subj "/CN=$dns" \
        -addext "subjectAltName=DNS:$dns" -addext "extendedKeyUsage=$purpose" \
        -keyout "$scratch/$name.key" -out "$scratch/$name.pem" 2>/dev/null
    openssl pkcs12 -export -out "$scratch/$name.pfx" -inkey "$scratch/$name.key" \
        -in "$scratch/$name.pem" -passout pass:
}
fingerprint() {
    openssl x509 -in "$scratch/$1.pem" -outform DER | openssl dgst -sha256 -r | cut -d ' ' -f 1
}
certificate broker broker serverAuth
certificate worker-client worker clientAuth
certificate edge-server localhost serverAuth
certificate edge-client edge clientAuth

install -m 0644 "$scratch/broker.pfx" "$HOSTING_CONFIG_DIR/broker/server.pfx"
install -m 0644 "$scratch/worker-client.pfx" "$HOSTING_CONFIG_DIR/worker/client.pfx"
install -m 0644 "$scratch/broker.pem" "$HOSTING_CONFIG_DIR/worker/ca.pem"
install -m 0644 "$scratch/edge-server.pfx" "$HOSTING_CONFIG_DIR/edge/server.pfx"
install -m 0644 "$scratch/edge-client.pfx" "$HOSTING_CONFIG_DIR/edge/client.pfx"
install -m 0644 "$scratch/broker.pem" "$HOSTING_CONFIG_DIR/edge/ca.pem"
chmod 755 "$HOSTING_CONFIG_DIR"/{broker,worker,edge}
for service in broker edge; do
    printf '{"version":1,"projects":[],"people":[]}\n' > "$HOSTING_CONFIG_DIR/$service/policy.json"
    chmod 644 "$HOSTING_CONFIG_DIR/$service/policy.json"
done

resource="https://localhost:$HOSTING_EDGE_PORT/mcp"
issuer="https://identity.invalid"
cat > "$HOSTING_CONFIG_DIR/broker.env" <<EOF_ENV
Broker__Authentication__Issuer=$issuer
Broker__Authentication__Audience=$resource
Broker__Authentication__Scope=sessions
Broker__Authentication__EdgeCertificateSha256=$(fingerprint edge-client)
Broker__Workers__smoke__Lane=primary
Broker__Workers__smoke__CertificateSha256=$(fingerprint worker-client)
EOF_ENV
cat > "$HOSTING_CONFIG_DIR/edge.env" <<EOF_ENV
Mcp__Authentication__Issuer=$issuer
Mcp__Authentication__Audience=$resource
Mcp__Authentication__Scope=sessions
SSL_CERT_FILE=/run/hosting/ca.pem
EOF_ENV
cat > "$HOSTING_CONFIG_DIR/worker.env" <<'EOF_ENV'
Worker__Profile=deterministic-primary
Worker__TrustScope=a0b6e647-964f-4370-a53b-4963dc3ca007
SSL_CERT_FILE=/run/hosting/ca.pem
EOF_ENV

"${compose[@]}" build
started=true
"${compose[@]}" up --detach
base="https://localhost:$HOSTING_EDGE_PORT"
request=(curl --silent --show-error --noproxy '*' --cacert "$scratch/edge-server.pem" --max-time 3)
for attempt in {1..30}; do
    if "${request[@]}" --fail "$base/health/live" >/dev/null 2>&1 &&
        "${compose[@]}" exec -T worker test -s /var/lib/hosting/runtime/worker-ledger.db &&
        "${compose[@]}" exec -T broker test -s /var/lib/hosting/broker.db; then
        break
    fi
    sleep 1
done
"${compose[@]}" exec -T worker test -s /var/lib/hosting/runtime/worker-ledger.db
"${compose[@]}" exec -T broker test -s /var/lib/hosting/broker.db
for service in broker worker edge; do
    if [[ $("${compose[@]}" exec -T "$service" id -u) -eq 0 ]]; then
        printf '%s is running as root\n' "$service" >&2
        exit 1
    fi
done

"${request[@]}" --fail "$base/.well-known/oauth-protected-resource/mcp" |
    jq -e --arg resource "$resource" '.resource == $resource and .authorization_servers == ["https://identity.invalid"]' >/dev/null
status=$("${request[@]}" -D "$scratch/headers" -o /dev/null -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{}' "$base/mcp")
[[ "$status" == 401 ]]
grep -qi '^www-authenticate: Bearer resource_metadata=' "$scratch/headers"
printf 'Container smoke checks passed.\n'
