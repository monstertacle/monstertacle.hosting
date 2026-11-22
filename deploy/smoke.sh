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
engine=docker
started=false
quadlet_units=""
quadlet_images=()
mode=${1:-}
if [[ "$mode" == "--quadlet" ]]; then
    engine=podman
fi
cleanup() {
    if [[ -n "$quadlet_units" ]]; then
        systemctl --user stop "$project-edge.service" "$project-worker.service" "$project-broker.service" >&2 || true
        find "$quadlet_units" -depth -delete
        systemctl --user daemon-reload
        local volume network
        for volume in "systemd-$project-broker-state" "systemd-$project-worker-state"; do
            if podman volume exists "$volume"; then
                podman volume rm "$volume" >&2
            fi
        done
        for network in "systemd-$project-private" "systemd-$project-outbound"; do
            if podman network exists "$network"; then
                podman network rm "$network" >&2
            fi
        done
    elif [[ "$started" == true ]]; then
        "${compose[@]}" down --volumes --remove-orphans >&2
    fi
    if (( ${#quadlet_images[@]} > 0 )); then
        podman image rm --ignore "${quadlet_images[@]}" >&2
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
install -m 0644 "$scratch/edge-server.pfx" "$HOSTING_CONFIG_DIR/edge/server.pfx"
install -m 0644 "$scratch/edge-client.pfx" "$HOSTING_CONFIG_DIR/edge/client.pfx"
# Follow the deployment trust model by adding the private broker root to the system roots.
cat /etc/ssl/certs/ca-certificates.crt "$scratch/broker.pem" > "$scratch/ca.pem"
install -m 0644 "$scratch/ca.pem" "$HOSTING_CONFIG_DIR/worker/ca.pem"
install -m 0644 "$scratch/ca.pem" "$HOSTING_CONFIG_DIR/edge/ca.pem"
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
Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics=Information
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
    for target in control-plane worker-host mcp-host; do
        image="localhost/$project-$target:local"
        podman build --target "$target" -t "$image" "$repo"
        quadlet_images+=("$image")
    done
    mkdir -p "${XDG_RUNTIME_DIR:?Set XDG_RUNTIME_DIR}/containers/systemd"
    quadlet_units=$(mktemp -d "$XDG_RUNTIME_DIR/containers/systemd/hosting-smoke.XXXXXXXX")
    for unit in "$repo"/deploy/quadlet/*; do
        filename=${unit##*/}
        sed -E -e "s|localhost/monstertacle-hosting-|localhost/$project-|g" \
            -e "s@^(After|Requires|Network|Volume)=hosting-@\1=$project-@" \
            -e "s|%h/\.config/monstertacle-hosting|$HOSTING_CONFIG_DIR|g" \
            -e "s|^PublishPort=127\.0\.0\.1:8443:8443$|PublishPort=127.0.0.1:$HOSTING_EDGE_PORT:8443|" \
            "$unit" > "$quadlet_units/$project-${filename#hosting-}"
    done
    systemctl --user daemon-reload
    systemctl --user start "$project-broker.service" "$project-worker.service" "$project-edge.service"
else
    "${compose[@]}" build
    started=true
    "${compose[@]}" up --detach
fi

container_id() {
    if [[ "$mode" == "--quadlet" ]]; then
        printf '%s\n' "systemd-$project-$1"
    else
        "${compose[@]}" ps --quiet "$1"
    fi
}

sqlite_ready() {
    local id=$1 path=$2 snapshot="$scratch/${2##*/}"
    "$engine" cp "$id:$path" "$snapshot" &&
        cmp --silent --bytes=16 "$snapshot" <(printf 'SQLite format 3\0')
}

verify_runtime() {
    local service=$1 id=$2
    "$engine" container inspect "$id" | jq -e '
        .[0] | .State.Running and .Config.User == "1654" and .HostConfig.ReadonlyRootfs
        and any(.Mounts[]; .Destination == "/run/hosting" and .RW == false)
    ' >/dev/null || { printf 'Unexpected runtime configuration for %s\n' "$service" >&2; return 1; }

    # Export inspects the image-backed directory below the volume mount.
    "$engine" export "$id" | tar --numeric-owner -tvf - | awk -v service="$service" '
        {
            path = $6
            sub(/^\.?\//, "", path)
            sub(/\/$/, "", path)
            if (path == "var/lib/hosting" && $1 == "drwx------" && $2 == "1654/0") state = 1
            if (path ~ /^(usr\/)?s?bin\/(sh|bash|dash|ash|zsh|ksh|busybox|apt|apt-get|dpkg|apk|dnf|yum|rpm|microdnf)$/) {
                print service ": unexpected executable " path > "/dev/stderr"
                unexpected = 1
            }
        }
        END {
            if (!state) print service ": state root must have mode 0700 and owner 1654:0" > "/dev/stderr"
            exit (!state || unexpected)
        }
    '

    "$engine" cp "$id:/etc/ssl/certs/ca-certificates.crt" "$scratch/$service-ca.pem"
    grep -q '^-----BEGIN CERTIFICATE-----$' "$scratch/$service-ca.pem"
    openssl crl2pkcs7 -nocrl -certfile "$scratch/$service-ca.pem" -out "$scratch/$service-ca.p7"
}

broker=$(container_id broker)
worker=$(container_id worker)
edge=$(container_id edge)
base="https://localhost:$HOSTING_EDGE_PORT"
request=(curl --silent --show-error --noproxy '*' --cacert "$scratch/edge-server.pem" --max-time 3)
for attempt in {1..30}; do
    if "${request[@]}" --fail "$base/health/live" >/dev/null 2>&1 &&
        "$engine" logs "$broker" 2>&1 |
            grep -E 'Request finished .* POST https://broker:8443/v1/worker/claims - 204 ' >/dev/null; then
        break
    fi
    if (( attempt == 30 )); then
        printf 'Timed out waiting for edge HTTPS and a successful worker claim over broker mTLS.\n' >&2
        exit 1
    fi
    sleep 1
done
sqlite_ready "$broker" /var/lib/hosting/broker.db
sqlite_ready "$worker" /var/lib/hosting/runtime/worker-ledger.db
verify_runtime broker "$broker"
verify_runtime worker "$worker"
verify_runtime edge "$edge"

"${request[@]}" --fail "$base/.well-known/oauth-protected-resource/mcp" |
    jq -e --arg resource "$resource" '.resource == $resource and .authorization_servers == ["https://identity.invalid"]' >/dev/null
status=$("${request[@]}" -D "$scratch/headers" -o /dev/null -w '%{http_code}' \
    -H 'Content-Type: application/json' -d '{}' "$base/mcp")
[[ "$status" == 401 ]]
grep -qi '^www-authenticate: Bearer resource_metadata=' "$scratch/headers"
printf 'Container smoke checks passed.\n'
