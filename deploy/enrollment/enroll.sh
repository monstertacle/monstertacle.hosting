#!/usr/bin/env bash
set +x
set -euo pipefail
umask 077
export LC_ALL=C

fail() { printf '%s\n' "$1" >&2; exit 1; }

[[ $# == 3 ]] || fail 'Usage: bash enroll.sh docker|podman CONFIG_DIRECTORY PRIVATE_REQUEST_FILE'
engine=$1
[[ "$engine" == docker || "$engine" == podman ]] || fail 'Choose Docker or Podman.'
config=$(realpath -ms -- "$2") || fail 'Select an existing private configuration directory.'
request=$(realpath -ms -- "$3") || fail 'Select an existing private request file.'
bundle=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
tag=$(cat "$bundle/version" 2>/dev/null) || fail 'Run enrollment from the extracted release.'
[[ "$tag" =~ ^deploy-v[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail 'Invalid release version.'
image="localhost/monstertacle-hosting-control-plane:$tag"
read -r build_id config_id < <(awk -F '\t' -v image="$image" '$1==image { print $2, $3 }' "$bundle/image-ids.tsv") ||
    fail 'The control-plane image is missing from the release manifest.'
read -r actual platform < <("$engine" image inspect --format '{{.Id}} {{.Os}}/{{.Architecture}}' "$image" 2>/dev/null) ||
    fail 'Load the verified control-plane image before enrollment.'
actual=sha256:${actual#sha256:}
[[ "$actual" =~ ^sha256:[a-f0-9]{64}$ && "$platform" == linux/amd64 &&
    ( "$actual" == "$build_id" || "$actual" == "$config_id" ) ]] ||
    fail 'The loaded control-plane image differs from the release manifest.'

owned_path() {
    [[ -O "$1" && "$(realpath -e -- "$1" 2>/dev/null)" == "$1" ]]
}

private_file() {
    local mode links size
    [[ -f "$1" ]] && owned_path "$1" || return 1
    read -r mode links size < <(stat -c '%a %h %s' -- "$1") || return 1
    (( (8#$mode & 077) == 0 && links == 1 && size > 0 && size <= 65536 ))
}

policy_directory="$config/policy"
policy="$policy_directory/policy.json"
if [[ ! -d "$config" ]] || ! owned_path "$config" || [[ "$(stat -c %a -- "$config")" != 700 ]]; then
    fail 'Use an owned, private config directory with mode 0700 and no symlinks.'
fi
if [[ ! -d "$policy_directory" ]] || ! owned_path "$policy_directory"; then
    fail 'Create the policy subdirectory with no symlinks.'
fi
mode=$(stat -c %a -- "$policy_directory")
(( (8#$mode & 07022) == 0 )) || fail 'Remove special modes and group or other write access from the policy directory.'
private_file "$request" || fail 'Use an owned private request file up to 64 KiB with no links.'

# Lock the directory inode for all cooperating writers, including first enrollment.
exec {lock}< "$policy_directory"
flock -x -w 30 "$lock" || fail 'Could not lock the policy directory. Retry after the other writer finishes.'
policy_metadata() {
    [[ -f "$policy" ]] && owned_path "$policy" && stat -c '%d:%i %a %h %s' -- "$policy"
}
before=$(policy_metadata) || fail 'Create an owned regular policy.json with no links.'
read -r _ mode links size <<< "$before"
(( (8#$mode & 07022) == 0 && links == 1 && size > 0 && size <= 262144 )) ||
    fail 'Use an unlinked version 1 policy file up to 256 KiB, writable only by the owner.'

work=$(mktemp -d "$policy_directory/.enrollment.XXXXXXXX")
cleanup() {
    for file in original candidate diagnostic; do
        if [[ -e "$work/$file" ]]; then unlink -- "$work/$file"; fi
    done
    rmdir -- "$work"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

cp -- "$policy" "$work/original"
status=0
{ cat -- "$request" && printf '\0' && cat -- "$work/original"; } |
    "$engine" run --rm -i --pull never --network none --read-only --log-driver none \
        --cap-drop ALL --security-opt no-new-privileges "$actual" policy-enrollment \
        > "$work/candidate" 2> "$work/diagnostic" || status=$?
case "$status" in
    0|3) ;;
    2)
        # The embedded command emits fixed diagnostics. Avoid forwarding engine output.
        diagnostic=$(cat -- "$work/diagnostic")
        case "$diagnostic" in
            'Invalid enrollment input.'|\
            'Supply an exact HTTPS issuer, opaque subject, principal ID and valid enrollment operation.'|\
            'The existing version 1 policy is missing or invalid.'|\
            'The enrollment request or existing policy is invalid JSON.'|\
            'Use the configured human issuer for enrollment.'|\
            'The exact identity and principal conflict with the existing policy.'|\
            'The principal and identity are not enrolled.'|\
            'The requested grants or resulting policy failed validation.'|\
            'The principal already has different grants. Revoke them before granting new access.')
                fail "$diagnostic" ;;
            *) fail 'The offline policy command failed.' ;;
        esac ;;
    *) fail 'The offline policy container could not complete.' ;;
esac

if [[ "$(policy_metadata)" != "$before" ]] || ! cmp -s -- "$policy" "$work/original"; then
    fail 'Policy changed during enrollment. Review the current policy before retrying.'
fi
if (( status == 3 )); then
    printf '%s\n' 'Policy already matches the requested enrollment.'
    exit 0
fi

[[ -s "$work/candidate" ]] || fail 'The offline command returned no policy.'
# Preserve the file's mode and group so both unprivileged services retain access.
chgrp --reference="$policy" -- "$work/candidate"
chmod --reference="$policy" -- "$work/candidate"
sync -- "$work/candidate"
# Both paths are on the same filesystem. Rename is the commit point.
mv -fT -- "$work/candidate" "$policy"
sync -- "$policy_directory"
printf '%s\n' 'Enrollment policy updated.'
