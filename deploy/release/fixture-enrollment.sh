#!/usr/bin/env bash
# Sourced by quickstart.sh.
# shellcheck disable=SC2154

enroll() {
    bash "$bundle/enrollment/enroll.sh" "$engine" "$HOSTING_CONFIG_DIR" "$scratch/$1.json"
}

prepare_enrollment() {
    mkdir -m 0755 "$HOSTING_CONFIG_DIR/policy"
    policy="$HOSTING_CONFIG_DIR/policy/policy.json"
    local legacy="$HOSTING_CONFIG_DIR/broker/policy.json"
    jq '.people[0] + {operation:"enroll"} | . + .identities[0] | del(.identities)' "$legacy" > "$scratch/first.json"
    jq '.people = []' "$legacy" > "$policy"
    chmod 0444 "$policy"
    # Any accidental fallback to the old role files must fail the fixture.
    unlink "$legacy"
    unlink "$HOSTING_CONFIG_DIR/edge/policy.json"
    enroll first
}

render_enrollment_dropins() {
    local role dir
    for role in broker edge; do
        dir="$units/$project-$role.container.d"
        mkdir -- "$dir"
        sed "s|%h/\.config/monstertacle-hosting|$HOSTING_CONFIG_DIR|g" \
            "$bundle/enrollment/quadlet/hosting-$role.container.d/50-enrollment.conf" > "$dir/50-enrollment.conf"
    done
}

verify_enrollment() {
    local one two initial second_session status=0
    initial=$(stat -c '%i' -- "$policy") || return 1
    enroll first && [[ "$(stat -c '%i' -- "$policy")" == "$initial" ]] || return 1

    jq '.id = "conflict"' "$scratch/first.json" > "$scratch/conflict.json"
    if enroll conflict; then return 1; fi
    [[ "$(stat -c '%i' -- "$policy")" == "$initial" ]] || return 1

    jq '.id = "second" | .subject = "unlisted" | .grants |= map(.actions = ["start","read"])' \
        "$scratch/first.json" > "$scratch/second.json"
    jq '.id = "third" | .subject = "fixture-third"' "$scratch/first.json" > "$scratch/third.json"
    enroll second & one=$!
    enroll third & two=$!
    wait "$one" || status=1
    wait "$two" || status=1
    (( status == 0 )) || return 1
    jq -e '[.people[].id] | sort == ["fixture-operator","second","third"]' "$policy" >/dev/null || return 1

    # A deliberately malformed version must not be replaced by the launcher.
    cp -- "$policy" "$scratch/last-good.json"
    printf '{"version":' > "$scratch/invalid-policy"
    chmod 0444 "$scratch/invalid-policy"
    mv -fT -- "$scratch/invalid-policy" "$policy"
    if enroll first; then return 1; fi
    [[ "$(cat -- "$policy")" == '{"version":' ]] || return 1
    chmod 0444 "$scratch/last-good.json"
    mv -fT -- "$scratch/last-good.json" "$policy"

    tool session_start "$(start_args)"
    [[ "$(post unlisted)" == 200 ]] || return 1
    second_session=$(read_result | jq -er '.structuredContent.sessionId | select(type == "string")') || return 1
    tool session_result "$(jq -cn --arg id "$second_session" '{sessionId:$id}')"
    [[ "$(post unlisted)" == 200 ]] && ! read_result >/dev/null || return 1
    jq -e '.result.isError == true and (.result.content[0].text | endswith("The requested session is unavailable."))' \
        "$scratch/rpc.json" >/dev/null || return 1

    jq '.operation = "revoke" | del(.grants)' "$scratch/first.json" > "$scratch/revoke.json"
    enroll revoke || return 1
    rpc initialize '{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"release fixture","version":"1"}}'
    [[ "$(post granted)" == 403 ]] || return 1
    tool session_start "$(start_args)"
    [[ "$(post granted)" == 403 ]] || return 1
    tool session_start "$(start_args)"
    [[ "$(post unlisted)" == 200 ]] && read_result >/dev/null || return 1
    enroll first || return 1
    tool session_start "$(start_args)"
    [[ "$(post granted)" == 200 ]] && read_result >/dev/null
}
