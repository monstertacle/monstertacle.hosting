#!/usr/bin/env bash
# Sourced by smoke.sh. These functions render only private fixture files.
# shellcheck disable=SC2154

volume_name() {
    local stack=$1 role=$2
    if [[ "$engine" == podman ]]; then
        printf 'systemd-%s-%s-state\n' "$stack" "$role"
    else
        printf '%s_%s-state\n' "$stack" "$role"
    fi
}

render_compose() {
    local stack=$1 service
    select_stack "$stack"
    "${compose[@]}" config --format json > "$scratch/$stack.json"
    jq -e --arg config "$HOSTING_CONFIG_DIR" --arg port "$HOSTING_EDGE_PORT" \
        --arg broker "$(volume_name "$stack" broker)" --arg worker "$(volume_name "$stack" worker)" '
        .networks.private.internal == true
        and (.volumes | keys == ["broker-state", "worker-state"])
        and (.volumes | all(.[]; .driver_opts == null and (.driver == null or .driver == "local")))
        and .volumes["broker-state"].name == $broker
        and .volumes["worker-state"].name == $worker
        and $broker != $worker
        and (.services | keys == ["broker", "edge", "worker"])
        and (.services | to_entries | all(. as $entry |
            $entry.key as $role | $entry.value |
            .network_mode == null and .privileged != true
            and ((.networks | keys) == (if $role == "worker" then ["private"] else ["outbound", "private"] end))
            and (.volumes | map(select(.type == "bind")) | length == 1 and
                .[0].source == ($config + "/" + $role) and
                .[0].target == "/run/hosting" and .[0].read_only == true)
            and (if $role == "edge" then
                (.volumes | length == 1) and
                (.ports | length == 1 and .[0].host_ip == "127.0.0.1" and
                    (.[0].published | tostring) == $port and
                    .[0].target == 8443 and .[0].protocol == "tcp")
            else
                ((.ports // []) | length == 0) and
                (.volumes | length == 2 and any(.[]; .type == "volume" and
                    .source == ($role + "-state") and .target == "/var/lib/hosting" and .read_only != true))
            end)))
    ' "$scratch/$stack.json" >/dev/null || fail "Compose fixture violates the mount, network or port contract."
    for service in broker worker edge; do
        jq -e --arg service "$service" --arg image "$(image_name "$service")" \
            '.services[$service].image == $image' "$scratch/$stack.json" >/dev/null
    done
}

verify_public_compose_override() {
    local stack=$1 hostname=edge.example.net
    local -a public_compose=("${compose[@]}" --file "$repo/deploy/public-ingress/compose.yaml")
    HOSTING_PUBLIC_HOSTNAME="$hostname" "${public_compose[@]}" config --format json > "$scratch/public-compose.json"
    jq -e --arg hostname "$hostname" --slurpfile base "$scratch/$stack.json" '
        . == ($base[0] | .services.edge.environment += {
            Mcp__PublicIngress__Enabled: "true",
            Mcp__PublicIngress__Hostname: $hostname
        })
    ' "$scratch/public-compose.json" >/dev/null || fail "Public ingress override changed the base deployment."
    if env -u HOSTING_PUBLIC_HOSTNAME "${public_compose[@]}" --env-file /dev/null config --quiet \
        > "$scratch/public-compose-missing.log" 2>&1; then
        fail "Public ingress override accepted a missing hostname."
    fi
    grep -Fq 'Set HOSTING_PUBLIC_HOSTNAME' "$scratch/public-compose-missing.log" ||
        fail "Public ingress override did not reject the missing hostname during interpolation."
}

quadlet_generator() {
    local candidate
    for candidate in /usr/lib/systemd/user-generators/podman-user-generator \
        /usr/lib/systemd/system-generators/podman-system-generator /usr/libexec/podman/quadlet; do
        if [[ -x "$candidate" ]]; then
            printf '%s\n' "$candidate"
            return
        fi
    done
    fail "Install Podman Quadlet to render the rootless fixture."
}

verify_quadlet_container() {
    local rendered=$1 stack=$2 role=$3
    local command index
    local -a args=() mounts=() ports=() networks=() env_files=()
    command=$(awk -v unit="---$stack-$role.service---" '
        /^---/ { active = ($0 == unit) }
        active && sub(/^ExecStart=/, "") { print }
    ' "$rendered")
    read -r -a args <<< "$command"
    [[ "${args[1]:-}" == run ]] || return 1
    [[ "${args[-1]}" == "$(image_name "$role")" ]] || return 1
    for ((index = 2; index < ${#args[@]} - 1; index++)); do
        case "${args[index]}" in
            -v|--volume) mounts+=("${args[++index]}") ;;
            -p|--publish) ports+=("${args[++index]}") ;;
            --network) networks+=("${args[++index]}") ;;
            --env-file) env_files+=("${args[++index]}") ;;
            --network-alias) ((++index)) ;;
            --mount*|--volumes-from*|--volume*|--publish*|-P|-v?*|-p?*|--net*|--pod*|--privileged*)
                return 1 ;;
        esac
    done
    [[ "${env_files[*]}" == "$HOSTING_CONFIG_DIR/$role.env" ]] || return 1
    [[ " ${mounts[*]} " == *" $HOSTING_CONFIG_DIR/$role:/run/hosting:ro,Z "* ]] || return 1
    if [[ "$role" == worker ]]; then
        [[ "${networks[*]}" == "systemd-$stack-private" ]] || return 1
    else
        [[ "${networks[*]}" == "systemd-$stack-private systemd-$stack-outbound" ]] || return 1
    fi
    if [[ "$role" == edge ]]; then
        [[ ${#mounts[@]} == 1 && "${ports[*]}" == "127.0.0.1:$HOSTING_EDGE_PORT:8443" ]]
    else
        [[ ${#mounts[@]} == 2 && ${#ports[@]} == 0 &&
            " ${mounts[*]} " == *" $(volume_name "$stack" "$role"):/var/lib/hosting:U "* ]]
    fi
}

render_quadlet() {
    local stack=$1 directory=$2 unit filename role rendered="$scratch/$1.units"
    # These fixture paths are substituted into Quadlet directives and parsed as single arguments.
    [[ "$HOSTING_CONFIG_DIR" =~ ^/[a-zA-Z0-9._/-]+$ ]] || fail "Use a simple absolute TMPDIR for Quadlet smoke."
    mkdir -p "$directory"
    for unit in "$repo"/deploy/quadlet/*.{container,volume,network}; do
        filename=${unit##*/}
        sed -E -e "s|localhost/monstertacle-hosting-|localhost/$project-|g" \
            -e "s@^(After|Requires|Network|Volume)=hosting-@\1=$stack-@" \
            -e "s|%h/\.config/monstertacle-hosting|$HOSTING_CONFIG_DIR|g" \
            -e "s|^PublishPort=127\.0\.0\.1:8443:8443$|PublishPort=127.0.0.1:$HOSTING_EDGE_PORT:8443|" \
            "$unit" > "$directory/$stack-${filename#hosting-}"
    done
    QUADLET_UNIT_DIRS="$directory" "$(quadlet_generator)" -user -dryrun \
        > "$rendered" 2> "$scratch/$stack.generator.log" || fail "Quadlet fixture rendering failed."
    for role in broker worker edge; do
        verify_quadlet_container "$rendered" "$stack" "$role" ||
            fail "Generated Quadlet $role fixture violates the mount, network or port contract."
    done
    grep -Eq "^ExecStart=[^ ]+ volume create --ignore $(volume_name "$stack" broker)$" "$rendered"
    grep -Eq "^ExecStart=[^ ]+ volume create --ignore $(volume_name "$stack" worker)$" "$rendered"
    grep -Eq "^ExecStart=[^ ]+ network create --ignore --internal systemd-$stack-private$" "$rendered"
}
