#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

fail() { printf '%s\n' "$*" >&2; exit 1; }

case "${1:-}" in
    compose) engine=docker ;;
    quadlet) engine=podman ;;
    *) fail "Usage: bash quickstart.sh compose|quadlet" ;;
esac
[[ $# == 1 && "$(uname -sm)" == 'Linux x86_64' ]] || fail "Select one engine on Linux x86-64."
# Fixture credentials always live outside the extracted tree.
bundle=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
scratch=$(mktemp -d /tmp/hosting-release.XXXXXXXX)
project="hosting-release-${scratch##*.}"
project=${project,,}
export HOSTING_CONFIG_DIR="$scratch/config"
export HOSTING_EDGE_PORT=${HOSTING_EDGE_PORT:-38443}
roles=(issuer broker worker worker-secondary edge)
state_roles=(broker worker worker-secondary)
started=false
units=""
# shellcheck source=deploy/smoke-config.sh
source "$bundle/smoke-config.sh"
# shellcheck source=deploy/release/fixture-config.sh
source "$bundle/fixture-config.sh"

compose=(docker compose --project-name "$project" --env-file /dev/null
    -f "$bundle/compose.yaml" -f "$bundle/fixture/compose.yaml")

cleanup() {
    local status=$? role kind name
    if [[ "$started" == true ]]; then
        if [[ "$engine" == docker ]]; then
            "${compose[@]}" down --volumes >/dev/null 2>&1 || status=1
        else
            for role in edge worker-secondary worker broker issuer; do
                systemctl --user stop "$project-$role.service" >/dev/null 2>&1 || status=1
            done
            for role in "${state_roles[@]}"; do
                systemctl --user stop "$project-$role-state-volume.service" >/dev/null 2>&1 || status=1
                name=$(volume_name "$project" "$role")
                if podman volume exists "$name"; then
                    podman volume rm "$name" >/dev/null 2>&1 || status=1
                fi
            done
            for kind in private outbound; do
                systemctl --user stop "$project-$kind-network.service" >/dev/null 2>&1 || status=1
                name="systemd-$project-$kind"
                if podman network exists "$name"; then
                    podman network rm "$name" >/dev/null 2>&1 || status=1
                fi
            done
        fi
    fi
    if [[ -n "$units" ]]; then
        rm -rf -- "$units" || status=1
        systemctl --user daemon-reload >/dev/null 2>&1 || status=1
    fi
    rm -rf -- "$scratch" || status=1
    if (( status != 0 )); then printf 'Fixture failed or cleanup was incomplete (%s).\n' "$project" >&2; fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ ! "$HOSTING_EDGE_PORT" =~ ^[1-9][0-9]{3,4}$ ]] ||
    (( HOSTING_EDGE_PORT < 1024 || HOSTING_EDGE_PORT > 65535 )); then
    fail "HOSTING_EDGE_PORT must be between 1024 and 65535."
fi

verify_images() {
    local tag ref build_id config_id actual platform target
    tag=$(cat "$bundle/version")
    [[ "$tag" =~ ^deploy-v[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Invalid bundle version."
    for target in control-plane mcp-host worker-host fixture-issuer; do
        ref="localhost/monstertacle-hosting-$target:$tag"
        read -r build_id config_id < <(awk -F '\t' -v ref="$ref" '$1==ref {print $2, $3}' "$bundle/image-ids.tsv")
        [[ "$build_id" =~ ^sha256:[0-9a-f]{64}$ && "$config_id" =~ ^sha256:[0-9a-f]{64}$ ]] ||
            fail "Invalid image manifest entry for $target."
        read -r actual platform < <("$engine" image inspect --format '{{.Id}} {{.Os}}/{{.Architecture}}' "$ref" 2>/dev/null) ||
            fail "Load the verified image archive before running the fixture."
        actual=sha256:${actual#sha256:}
        [[ "$platform" == linux/amd64 && ( "$actual" == "$build_id" || "$actual" == "$config_id" ) ]] ||
            fail "The loaded $target image differs from the bundle manifest."
    done
}

verify_images
mkdir "$HOSTING_CONFIG_DIR"
issuer=https://issuer:8443
resource="https://localhost:$HOSTING_EDGE_PORT/mcp"
subject="granted-$project"
exec 3>&2
phase="private bootstrap"
trap 'printf "Fixture step failed: %s.\n" "$phase" >&3' ERR
make_fixture > "$scratch/bootstrap.log" 2>&1

start_fixture() {
    local role
    if [[ "$engine" == docker ]]; then
        "${compose[@]}" config --quiet
        started=true
        "${compose[@]}" up --detach --no-build --pull never
    else
        [[ "$(podman info --format '{{.Host.Security.Rootless}}')" == true ]] || fail "Use rootless Podman."
        mkdir -p "${XDG_RUNTIME_DIR:?}/containers/systemd"
        units=$(mktemp -d "$XDG_RUNTIME_DIR/containers/systemd/$project.XXXXXXXX")
        render_quadlet_files "$project" "$units" "$bundle/quadlet"
        render_quadlet_files "$project" "$units" "$bundle/fixture/quadlet"
        systemctl --user daemon-reload
        started=true
        local -a services=()
        for role in "${roles[@]}"; do services+=("$project-$role.service"); done
        systemctl --user start "${services[@]}"
    fi
}
phase="engine startup"
start_fixture > "$scratch/engine.log" 2>&1
trap - ERR
exec 3>&-

# Check provisioned storage identities in the running engine before asserting results.
for role in "${state_roles[@]}"; do
    expected_volume=$(volume_name "$project" "$role")
    if [[ "$engine" == docker ]]; then
        container=$("${compose[@]}" ps --quiet "$role")
    else
        container="systemd-$project-$role"
    fi
    "$engine" container inspect "$container" | jq -e --arg volume "$expected_volume" '
        .[0] | [.Mounts[] | select(.Type=="volume")] as $mounts
        | .State.Running and .HostConfig.ReadonlyRootfs and .Config.User == "1654"
        and ((.HostConfig.PortBindings // {}) | length == 0)
        and ($mounts | length == 1 and .[0].Name == $volume and .[0].Destination == "/var/lib/hosting")
    ' >/dev/null || fail "Unexpected state mount or port for $role."
done

post() {
    local header=$1
    local -a authorization=()
    if [[ -n "$header" ]]; then authorization=(-H "@$scratch/$header.header"); fi
    curl --disable --silent --noproxy '*' --cacert "$scratch/edge-server.pem" --max-time 5 \
        -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -H 'MCP-Protocol-Version: 2025-11-25' "${authorization[@]}" --data-binary "@$scratch/request.json" \
        -o "$scratch/response" -w '%{http_code}' "$resource"
}

rpc() {
    local method=$1 params=$2
    jq -cn --arg method "$method" --argjson params "$params" \
        '{jsonrpc:"2.0",id:1,method:$method,params:$params}' > "$scratch/request.json"
}

tool() {
    local name=$1 arguments=$2
    rpc tools/call "$(jq -cn --arg name "$name" --argjson arguments "$arguments" '{name:$name,arguments:$arguments}')"
}

read_result() {
    if ! jq -e '.id == 1' "$scratch/response" >/dev/null 2>&1; then
        sed -n 's/^data:[[:blank:]]*//p' "$scratch/response" > "$scratch/rpc.json"
    else
        cp "$scratch/response" "$scratch/rpc.json"
    fi
    jq -ce 'select(.id == 1 and .error == null and .result.isError != true) | .result' "$scratch/rpc.json" 2>/dev/null
}

rpc initialize '{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"release fixture","version":"1"}}'
ready=false
for (( attempt=0; attempt<45; attempt++ )); do
    if [[ "$(post granted || true)" == 200 ]] && read_result >/dev/null; then
        ready=true
        break
    fi
    sleep 1
done
[[ "$ready" == true ]] || fail "Timed out waiting for authenticated MCP initialization."

start_args() {
    jq -cn --arg mutationId "$(cat /proc/sys/kernel/random/uuid)" '{mutationId:$mutationId,project:"fixture",task:"release fixture"}'
}

tool session_start "$(start_args)"
[[ "$(post '')" == 401 ]] || fail "Anonymous session start did not return 401."
tool session_start "$(start_args)"
[[ "$(post wrong-audience)" == 401 ]] || fail "Wrong-audience session start did not return 401."
tool session_start "$(start_args)"
[[ "$(post unlisted)" == 403 ]] || fail "Unlisted subject session start did not return 403."
printf 'Admission denied for anonymous, wrong-audience and unlisted requests.\n'

tool session_start "$(start_args)"
[[ "$(post granted)" == 200 ]] || fail "Granted session start did not return 200."
session=$(read_result | jq -er '.structuredContent.sessionId | select(type=="string")') ||
    fail "Granted session start did not return a session ID."
printf 'Granted JWT admitted a session through the MCP edge.\n'

session_args=$(jq -cn --arg sessionId "$session" '{sessionId:$sessionId}')
tool session_result "$session_args"
complete=false
for (( attempt=0; attempt<60; attempt++ )); do
    if [[ "$(post granted)" == 200 ]] && read_result > "$scratch/manifest.json" &&
        jq -e --arg session "$session" '
            .structuredContent | (.sha256 | test("^[0-9a-f]{64}$"))
            and (.manifest | .complete and .sessionId == $session
                and ([.jobs[].status.lane] | sort == ["primary","secondary"])
                and all(.jobs[]; .status.state == "succeeded" and .result.manifest.artifacts[0].name == "output"))
        ' "$scratch/manifest.json" >/dev/null 2>&1; then
        complete=true
        break
    fi
    sleep 1
done
[[ "$complete" == true ]] || fail "The broker did not return two successful lane results."
for lane in primary secondary; do
    job=$(jq -er --arg lane "$lane" '.structuredContent.manifest.jobs[].status | select(.lane == $lane) | .jobId' "$scratch/manifest.json")
    tool session_artifact "$(jq -c --arg jobId "$job" '. + {jobId:$jobId,name:"output"}' <<< "$session_args")"
    [[ "$(post granted)" == 200 ]] || fail "Artifact read failed for $lane."
    read_result | jq -er '.structuredContent | select(.artifact.name == "output") | .content' |
        base64 --decode > "$scratch/artifact" || fail "Invalid artifact for $lane."
    printf 'deterministic-%s: release fixture' "$lane" | cmp --silent - "$scratch/artifact" ||
        fail "Unexpected deterministic output for $lane."
done
printf 'Both worker lanes succeeded. Verified result and both artifacts retrieved (%s).\n' "$1"
