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
quadlet_units=""
mode=${1:-}
cleanup() {
    if [[ -n "$quadlet_units" ]]; then
        systemctl --user stop hosting-edge.service hosting-worker.service hosting-broker.service >&2 || true
        find "$quadlet_units" -depth -delete
        systemctl --user daemon-reload
    elif [[ "$started" == true ]]; then
        "${compose[@]}" down --volumes --remove-orphans >&2
    fi
    find "$scratch" -depth -delete
}
trap cleanup EXIT

if [[ $# -gt 1 || ( -n "$mode" && "$mode" != "--config-only" && "$mode" != "--quadlet" ) ]]; then
    printf 'Usage: %s [--config-only|--quadlet]\n' "$0" >&2
    exit 2
fi

mkdir -p "$HOSTING_CONFIG_DIR"/{broker,worker,edge}
for name in broker worker edge; do
    touch "$HOSTING_CONFIG_DIR/$name.env"
done
if [[ "$mode" != "--quadlet" ]]; then
    "${compose[@]}" config --quiet
fi
if [[ "$mode" == "--config-only" ]]; then
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

if [[ "$mode" == "--quadlet" ]]; then
    test "$(podman info --format '{{.Host.Security.Rootless}}')" = true
    systemctl --user show-environment >/dev/null
    for unit in hosting-broker hosting-worker hosting-edge; do
        if [[ $(systemctl --user show -p LoadState --value "$unit.service") != not-found ]] ||
            podman container exists "systemd-$unit"; then
            printf 'Quadlet smoke requires an unused unit and container name: %s\n' "$unit" >&2
            exit 1
        fi
    done
    for state in broker worker; do
        if podman volume exists "systemd-hosting-$state-state"; then
            printf 'Quadlet smoke requires an unused volume: %s\n' "$state" >&2
            exit 1
        fi
    done
    for target in control-plane worker-host mcp-host; do
        podman build --target "$target" -t "localhost/monstertacle-hosting-$target:local" "$repo"
    done
    mkdir -p "${XDG_RUNTIME_DIR:?Set XDG_RUNTIME_DIR}/containers/systemd"
    quadlet_units=$(mktemp -d "$XDG_RUNTIME_DIR/containers/systemd/hosting-smoke.XXXXXXXX")
    for unit in "$repo"/deploy/quadlet/*; do
        sed -e "s|%h/\.config/monstertacle-hosting|$HOSTING_CONFIG_DIR|g" \
            -e "s|^PublishPort=127\.0\.0\.1:8443:8443$|PublishPort=127.0.0.1:$HOSTING_EDGE_PORT:8443|" \
            "$unit" > "$quadlet_units/${unit##*/}"
    done
    systemctl --user daemon-reload
    systemctl --user start hosting-broker.service hosting-worker.service hosting-edge.service
else
    "${compose[@]}" build
    started=true
    "${compose[@]}" up --detach
fi

exec_service() {
    local service=$1
    shift
    if [[ "$mode" == "--quadlet" ]]; then
        podman exec "systemd-hosting-$service" "$@"
    else
        "${compose[@]}" exec -T "$service" "$@"
    fi
}
base="https://localhost:$HOSTING_EDGE_PORT"
request=(curl --silent --show-error --noproxy '*' --cacert "$scratch/edge-server.pem" --max-time 3)
for attempt in {1..30}; do
    if "${request[@]}" --fail "$base/health/live" >/dev/null 2>&1 &&
        exec_service worker test -s /var/lib/hosting/runtime/worker-ledger.db &&
        exec_service broker test -s /var/lib/hosting/broker.db; then
        break
    fi
    sleep 1
done
exec_service worker test -s /var/lib/hosting/runtime/worker-ledger.db
exec_service broker test -s /var/lib/hosting/broker.db
for service in broker worker edge; do
    if [[ $(exec_service "$service" id -u) -eq 0 ]]; then
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
