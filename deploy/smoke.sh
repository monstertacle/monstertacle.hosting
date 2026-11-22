#!/usr/bin/env bash
set -euo pipefail
umask 077

engine=docker
config_only=false
for option in "$@"; do
    case "$option" in
        --quadlet) engine=podman ;;
        --config-only) config_only=true ;;
        *) printf 'Usage: %s [--quadlet] [--config-only]\n' "$0" >&2; exit 2 ;;
    esac
done

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
scratch=$(mktemp -d "${TMPDIR:-/tmp}/hosting-smoke.XXXXXXXX")
project="hosting-smoke-${scratch##*.}"
project=${project,,}
restored="$project-restored"
export HOSTING_CONFIG_DIR="$scratch/config"
export HOSTING_EDGE_PORT="${HOSTING_EDGE_PORT:-28443}"
sdk=mcr.microsoft.com/dotnet/sdk:11.0
compose=()
installed=()
images=()
restore_volumes=()
quadlet_units=""

# shellcheck source=deploy/smoke-config.sh
source "$repo/deploy/smoke-config.sh"

image_name() {
    local target
    case "$1" in
        broker) target=control-plane ;;
        worker) target=worker-host ;;
        edge) target=mcp-host ;;
    esac
    printf 'localhost/%s-%s:local\n' "$project" "$target"
}

select_stack() {
    stack=$1
    if [[ "$engine" == docker ]]; then
        compose=(docker compose --project-name "$stack" --file "$repo/deploy/compose.yaml"
            --file "$scratch/$project-images.yaml")
        if [[ "$stack" == "$restored" ]]; then
            compose+=(--file "$scratch/$restored-volumes.yaml")
        fi
        private_network="${stack}_private"
    else
        private_network="systemd-$stack-private"
    fi
}

stack_units() {
    systemctl --user "$1" "$stack-edge.service" "$stack-worker.service" "$stack-broker.service"
}

cleanup() {
    local status=$? name role volume network image
    for name in "${installed[@]}"; do
        select_stack "$name"
        if [[ "$engine" == podman ]]; then
            stack_units stop >&2 || status=1
            for role in broker worker; do
                volume=$(volume_name "$name" "$role")
                if podman volume exists "$volume"; then
                    podman volume rm "$volume" >&2 || status=1
                fi
            done
            for network in "$private_network" "systemd-$name-outbound"; do
                if podman network exists "$network"; then
                    podman network rm "$network" >&2 || status=1
                fi
            done
        else
            "${compose[@]}" down --volumes >&2 || status=1
        fi
    done
    if [[ -n "$quadlet_units" ]]; then
        find "$quadlet_units" -depth -delete || status=1
        systemctl --user daemon-reload || status=1
    fi
    for volume in "${restore_volumes[@]}"; do
        if "$engine" volume inspect "$volume" >/dev/null 2>&1; then
            "$engine" volume rm "$volume" >&2 || status=1
        fi
    done
    for image in "${images[@]}"; do
        if "$engine" image inspect "$image" >/dev/null 2>&1; then
            "$engine" image rm "$image" >&2 || status=1
        fi
    done
    find "$scratch" -depth -delete || status=1
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ ! "$HOSTING_EDGE_PORT" =~ ^[1-9][0-9]{0,4}$ ]] || (( HOSTING_EDGE_PORT > 65535 )); then
    fail "HOSTING_EDGE_PORT must be a TCP port number."
fi
mkdir -p "$HOSTING_CONFIG_DIR"/{broker,worker,edge}
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

mkdir "$scratch/probe"
cp "$scratch/broker.pem" "$scratch/edge-client.pfx" "$scratch/probe/"

if [[ "$engine" == podman ]]; then
    render_quadlet "$project" "$scratch/initial-units"
    render_quadlet "$restored" "$scratch/restored-units"
else
    {
        printf 'services:\n'
        for role in broker worker edge; do
            printf '  %s:\n    image: %s\n' "$role" "$(image_name "$role")"
        done
    } > "$scratch/$project-images.yaml"
    {
        printf 'volumes:\n'
        for role in broker worker; do
            printf '  %s-state:\n    external: true\n    name: %s\n' "$role" "$(volume_name "$restored" "$role")"
        done
    } > "$scratch/$restored-volumes.yaml"
    render_compose "$project"
    render_compose "$restored"
fi
printf 'Preflight passed for %s.\n' "$engine"
if [[ "$config_only" == true ]]; then
    exit 0
fi

build_images() {
    local role target
    for role in broker worker edge; do
        images+=("$(image_name "$role")")
    done
    if [[ "$engine" == podman ]]; then
        for target in control-plane worker-host mcp-host; do
            podman build --target "$target" -t "localhost/$project-$target:local" "$repo"
        done
    else
        "${compose[@]}" build
    fi
}

start_stack() {
    if [[ "$engine" == podman ]]; then
        stack_units start
    else
        "${compose[@]}" up --detach --no-build
    fi
}

stop_stack() {
    if [[ "$engine" == podman ]]; then
        stack_units stop
    else
        "${compose[@]}" down
    fi
}

container_id() {
    local name
    if [[ "$engine" == podman ]]; then
        name="systemd-$stack-$1"
    else
        name=$("${compose[@]}" ps --quiet "$1")
    fi
    "$engine" container inspect --format '{{.Id}}' "$name"
}

runtime_identity() {
    local service=$1 id volume=""
    id=$(container_id "$service")
    if [[ "$service" != edge ]]; then
        volume=$(volume_name "$stack" "$service")
    fi
    "$engine" container inspect "$id" | jq -er --arg volume "$volume" --arg port "$HOSTING_EDGE_PORT" \
        --arg config "$HOSTING_CONFIG_DIR/$service" '
        .[0] | select(.State.Running and .Config.User == "1654" and .HostConfig.ReadonlyRootfs
            and ((.HostConfig.PortBindings // {}) |
                if $volume == "" then . == {"8443/tcp": [{"HostIp": "127.0.0.1", "HostPort": $port}]}
                else length == 0 end)
            and any(.Mounts[]; .Type == "bind" and .Source == $config and
                .Destination == "/run/hosting" and .RW == false)
            and ([.Mounts[] | select(.Type == "volume" or
                (.Type == "bind" and .Destination != "/run/hosting"))] |
                if $volume == "" then length == 0 else length == 1 and
                    .[0].Type == "volume" and .[0].Name == $volume and
                    .[0].Destination == "/var/lib/hosting" and .[0].RW == true end)) |
        [.Id, .State.StartedAt, $volume] | @tsv
    ' || fail "Unexpected $service runtime identity or mounts."
}
verify_runtime() {
    local service=$1 id=$2

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

base="https://localhost:$HOSTING_EDGE_PORT"
request=(curl --silent --show-error --noproxy '*' --cacert "$scratch/edge-server.pem" --max-time 3)

broker_probe() {
    local code
    local -a probe=("$engine" run --rm --network "$private_network" --read-only
        --volume "$scratch/probe:/probe:ro,Z" "$sdk"
        curl --silent --show-error --noproxy '*' --cacert /probe/broker.pem
        --max-time 5 -o /dev/null -w '%{http_code}')
    if "${probe[@]}" https://broker:8443/health/live >/dev/null 2>&1; then
        fail "Broker accepted a TLS connection without a client certificate."
    fi
    code=$("${probe[@]}" --cert-type P12 --cert /probe/edge-client.pfx: https://broker:8443/health/live)
    [[ "$code" == 204 ]] || fail "Broker rejected fixture edge mTLS."
    code=$("${probe[@]}" --cert-type P12 --cert /probe/edge-client.pfx: https://broker:8443/v1/sessions)
    [[ "$code" == 401 ]] || fail "Broker session route did not require a bearer token from the edge peer."
}

verify_transport() {
    local since=$1 broker=$2 attempt status
    for attempt in {1..30}; do
        if "${request[@]}" --fail "$base/health/live" >/dev/null 2>&1 &&
            "$engine" logs --since "$since" "$broker" 2>&1 |
                grep -E 'Request finished .* POST https://broker:8443/v1/worker/claims - 204 ' >/dev/null; then
            break
        fi
        if (( attempt == 30 )); then
            fail "Timed out waiting for edge HTTPS and a fresh successful worker claim over broker mTLS."
        fi
        sleep 1
    done
    "${request[@]}" --fail "$base/.well-known/oauth-protected-resource/mcp" |
        jq -e --arg resource "$resource" --arg issuer "$issuer" \
            '.resource == $resource and .authorization_servers == [$issuer]' >/dev/null
    status=$("${request[@]}" -D "$scratch/headers" -o /dev/null -w '%{http_code}' \
        -H 'Content-Type: application/json' -d '{}' "$base/mcp")
    [[ "$status" == 401 ]] || fail "Anonymous MCP response was $status."
    grep -qi '^www-authenticate: Bearer resource_metadata=' "$scratch/headers"
    broker_probe
    printf 'Transport verified: edge HTTPS and discovery, worker and edge mTLS, anonymous MCP 401.\n'
}

volume_tool() {
    local volume=$1 access=$2
    shift 2
    local -a stdin=()
    if [[ "$access" == rw ]]; then
        stdin=(--interactive)
    fi
    "$engine" run --rm "${stdin[@]}" --network none --read-only --user 0:0 \
        --volume "$volume:/state:$access,nocopy" "$sdk" "$@"
}

state_file() {
    case "$1" in
        broker) printf 'broker.db\n' ;;
        worker) printf 'runtime/worker-ledger.db\n' ;;
    esac
}

verify_state() {
    local role=$1 volume=$2 marker=${3:-}
    # shellcheck disable=SC2016
    volume_tool "$volume" ro bash -euc '
        read -r mode owner < <(stat -c "%a %u" /state)
        database_owner=$(stat -c "%u" "/state/$1")
        if [[ "$owner" != "1654" || "$database_owner" != "1654" ]] || (( 8#$mode & 0022 )); then
            printf "Unexpected state volume metadata: directory mode %s owner %s, database owner %s\n" \
                "$mode" "$owner" "$database_owner" >&2
            exit 1
        fi
        if [[ -n "$2" ]]; then
            test "$(stat -c "%a %u" /state/.lifecycle-marker)" = "600 1654"
            test "$(cat /state/.lifecycle-marker)" = "$2"
        fi
    ' bash "$(state_file "$role")" "$marker" || fail "$role state verification failed in $volume."
}

quiescent() {
    local role volume writers
    for role in broker worker; do
        volume=$(volume_name "$stack" "$role")
        "$engine" volume inspect "$volume" >/dev/null
        writers=$("$engine" ps --quiet --filter "volume=$volume")
        [[ -z "$writers" ]] || fail "A running container still mounts $volume."
    done
}

record_stage() {
    local stage=$1
    broker_identity=$(runtime_identity broker)
    worker_identity=$(runtime_identity worker)
    edge_identity=$(runtime_identity edge)
    printf '%s broker\t%s\n%s worker\t%s\n%s edge\t%s\n' \
        "$stage" "$broker_identity" "$stage" "$worker_identity" "$stage" "$edge_identity"
}

verify_recreated() {
    [[ "${broker_identity%%$'\t'*}" != "${previous_broker%%$'\t'*}" &&
        "${worker_identity%%$'\t'*}" != "${previous_worker%%$'\t'*}" &&
        "${edge_identity%%$'\t'*}" != "${previous_edge%%$'\t'*}" ]] ||
        fail "Expected new container IDs for all three services."
}

verify_running_state() {
    verify_state broker "$(volume_name "$stack" broker)" "${1:-}"
    verify_state worker "$(volume_name "$stack" worker)" "${2:-}"
}

select_stack "$project"
if [[ "$engine" == podman ]]; then
    [[ "$(podman info --format '{{.Host.Security.Rootless}}')" == true ]] || fail "Quadlet smoke requires rootless Podman."
    systemctl --user show-environment >/dev/null
fi
build_images
if [[ "$engine" == podman ]]; then
    mkdir -p "${XDG_RUNTIME_DIR:?Set XDG_RUNTIME_DIR}/containers/systemd"
    quadlet_units=$(mktemp -d "$XDG_RUNTIME_DIR/containers/systemd/hosting-smoke.XXXXXXXX")
    cp "$scratch"/{initial,restored}-units/* "$quadlet_units"
    systemctl --user daemon-reload
fi
installed+=("$project")
since=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
start_stack
record_stage initial
verify_transport "$since" "${broker_identity%%$'\t'*}"
verify_running_state
for role in broker worker edge; do
    verify_runtime "$role" "$(container_id "$role")"
done

previous_broker=$broker_identity previous_worker=$worker_identity previous_edge=$edge_identity
certificate edge-replacement localhost serverAuth
install -m 0644 "$scratch/edge-replacement.pfx" "$HOSTING_CONFIG_DIR/edge/server.next"
mv -f "$HOSTING_CONFIG_DIR/edge/server.next" "$HOSTING_CONFIG_DIR/edge/server.pfx"
if [[ "$engine" == podman ]]; then
    systemctl --user restart "$stack-edge.service"
else
    "${compose[@]}" up --detach --no-build --no-deps --force-recreate edge
fi
record_stage edge-replaced
[[ "${edge_identity%%$'\t'*}" != "${previous_edge%%$'\t'*}" &&
    "$broker_identity" == "$previous_broker" && "$worker_identity" == "$previous_worker" ]] ||
    fail "Edge replacement changed the broker or worker, or reused the old edge container."
old_request=("${request[@]}")
request=(curl --silent --show-error --noproxy '*' --cacert "$scratch/edge-replacement.pem" --max-time 3)
since=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
verify_transport "$since" "${broker_identity%%$'\t'*}"
[[ "$(runtime_identity broker)" == "$previous_broker" &&
    "$(runtime_identity worker)" == "$previous_worker" ]] ||
    fail "Broker or worker identity changed during edge verification."
if "${old_request[@]}" --fail "$base/health/live" >/dev/null 2>&1; then
    fail "Edge still presents the original TLS certificate."
fi

previous_broker=$broker_identity previous_worker=$worker_identity previous_edge=$edge_identity
# A fixture marker is independent of the application's SQLite files.
for role in broker worker; do
    # shellcheck disable=SC2016
    volume_tool "$(volume_name "$stack" "$role")" rw bash -euc '
        umask 077
        printf "%s\n" "$1" > /state/.lifecycle-marker
        chown 1654:1654 /state/.lifecycle-marker
    ' bash "$project-$role"
done
verify_running_state "$project-broker" "$project-worker"
stop_stack
quiescent
since=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
start_stack
record_stage stack-recreated
verify_recreated
verify_transport "$since" "${broker_identity%%$'\t'*}"
verify_running_state "$project-broker" "$project-worker"

previous_broker=$broker_identity previous_worker=$worker_identity previous_edge=$edge_identity
stop_stack
quiescent
for role in broker worker; do
    original_volume=$(volume_name "$project" "$role")
    restore_volume=$(volume_name "$restored" "$role")
    archive="$scratch/$role.tar"
    # shellcheck disable=SC2016
    volume_tool "$original_volume" ro bash -euc '
        cmp --silent --bytes=16 "/state/$1" <(printf "SQLite format 3\0")
        tar --sort=name --numeric-owner --format=gnu -C /state -cf - .
    ' bash "$(state_file "$role")" > "$archive"

    if "$engine" volume inspect "$restore_volume" >/dev/null 2>&1; then
        fail "Restore volume already exists: $restore_volume."
    fi
    "$engine" volume create "$restore_volume" >/dev/null
    restore_volumes+=("$restore_volume")
    # shellcheck disable=SC2016
    volume_tool "$restore_volume" rw bash -euc '
        test -z "$(ls -A /state)"
        tar --numeric-owner --same-owner --same-permissions -C /state -xf -
    ' < "$archive"

    expected_digest=$(sha256sum < "$archive")
    for volume in "$original_volume" "$restore_volume"; do
        verify_state "$role" "$volume" "$project-$role"
        actual_digest=$(volume_tool "$volume" ro tar --sort=name --numeric-owner --format=gnu -C /state -cf - . | sha256sum)
        [[ "$expected_digest" == "$actual_digest" ]] || fail "Archive differs from volume $volume."
    done
    sidecars=$(tar -tf "$archive" | awk '/\.db-(wal|shm|journal)$/ { count++ } END { print count + 0 }')
    printf 'Archive verified %s -> %s (SHA-256 %s, SQLite sidecars %s)\n' \
        "$original_volume" "$restore_volume" "${expected_digest%% *}" "$sidecars"
done
select_stack "$restored"
installed+=("$restored")
since=$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)
start_stack
record_stage stack-restored
verify_recreated
verify_transport "$since" "${broker_identity%%$'\t'*}"
verify_running_state "$project-broker" "$project-worker"
printf 'Container deployment lifecycle passed on %s.\n' "$engine"
