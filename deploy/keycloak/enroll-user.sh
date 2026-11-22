#!/usr/bin/env bash
set +x
set -euo pipefail
umask 077

fail() { printf '%s\n' "$1" >&2; exit 1; }

[[ $# == 4 || $# == 5 ]] || fail 'Usage: bash enroll-user.sh create|lookup USERNAME_OR_ID ADMIN_TOKEN_FILE PRIVATE_HANDOFF [PASSWORD_FILE]'
operation=$1
account=$2
token_file=$3
handoff=$4
password_file=${5:-}
issuer=${KEYCLOAK_PUBLIC_ORIGIN:-}/realms/hosting
admin=${KEYCLOAK_ADMIN_ORIGIN:-}
origin_pattern='^https://[A-Za-z0-9.-]+(:[1-9][0-9]{0,4})?$'
[[ "${KEYCLOAK_PUBLIC_ORIGIN:-}" =~ $origin_pattern && "$admin" =~ $origin_pattern &&
    "$KEYCLOAK_PUBLIC_ORIGIN" != "$admin" ]] || fail 'Supply distinct trusted HTTPS login and private admin origins.'

private_file() {
    local absolute mode links size
    absolute=$(realpath -ms -- "$1") || return 1
    [[ -f "$absolute" && -O "$absolute" && "$(realpath -e -- "$1")" == "$absolute" ]] || return 1
    read -r mode links size < <(stat -c '%a %h %s' -- "$absolute") || return 1
    (( (8#$mode & 077) == 0 && links == 1 && size > 0 && size <= 65536 ))
}

private_file "$token_file" || fail 'Use an owned, protected, unlinked admin token file.'
token=$(< "$token_file")
[[ "$token" =~ ^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$ ]] || fail 'Provide a short-lived realm admin access token.'
out_dir=$(dirname -- "$handoff")
[[ -d "$out_dir" && -O "$out_dir" && "$(stat -c %a -- "$out_dir")" == 700 ]] ||
    fail 'Keep the handoff in an operator-owned directory with mode 0700.'
[[ ! -e "$handoff" && ! -L "$handoff" ]] || fail 'Choose a new private handoff file.'

work=$(mktemp -d)
cleanup() {
    for name in header password request headers response subject; do
        if [[ -e "$work/$name" ]]; then unlink "$work/$name"; fi
    done
    rmdir "$work"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
printf 'Authorization: Bearer %s\n' "$token" > "$work/header"
unset token
curl_args=(--silent --proto '=https' --noproxy '*' --connect-timeout 5 --max-time 20
    -H "@$work/header" -D "$work/headers" -o "$work/response" -w '%{http_code}')
if [[ -n "${KEYCLOAK_CA_FILE:-}" ]]; then curl_args+=(--cacert "$KEYCLOAK_CA_FILE"); fi
users="$admin/admin/realms/hosting/users"
uuid='[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}'

case "$operation" in
    create)
        [[ "$account" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$ ]] || fail 'Choose a simple local username.'
        if [[ -n "$password_file" ]]; then
            private_file "$password_file" || fail 'Use an owned, protected, unlinked password file.'
            password=$(< "$password_file")
        else
            read -rs -p 'Initial account password: ' password </dev/tty || fail 'Provide a password at the private prompt or through a protected file.'
            printf '\n' >&2
        fi
        if (( ${#password} < 12 || ${#password} > 256 )) || [[ "$password" == *$'\n'* || "$password" == *$'\r'* ]]; then
            fail 'Provide a one-line password of 12 to 256 characters.'
        fi
        printf '%s' "$password" > "$work/password"
        unset password
        jq -n --arg username "$account" --rawfile password "$work/password" '
            {username:$username,enabled:true,credentials:[{type:"password",value:$password,temporary:false}]}
        ' > "$work/request"
        status=$(curl --disable "${curl_args[@]}" -H 'Content-Type: application/json' \
            --data-binary "@$work/request" "$users") || fail 'The private HTTPS admin endpoint could not be reached.'
        [[ "$status" != 409 ]] || fail 'The account already exists. Confirm the stable user ID privately before using lookup.'
        [[ "$status" == 201 ]] || fail 'Keycloak declined creation. Check realm permissions and password policy.'
        location=$(awk 'tolower($1) == "location:" { sub(/^[^:]+:[[:blank:]]*/, ""); sub(/\r$/, ""); print }' "$work/headers")
        [[ "$location" =~ ^(https://[A-Za-z0-9.:-]+)?/admin/realms/hosting/users/($uuid)$ ]] ||
            fail 'Account creation returned an unexpected identifier. Confirm the user through private administration.'
        subject=${BASH_REMATCH[2]}
        ;;
    lookup)
        [[ $# == 4 && "$account" =~ ^$uuid$ ]] || fail 'Look up a previously verified Keycloak user ID.'
        subject=$account
        ;;
    *) fail 'Choose create or lookup.' ;;
esac

status=$(curl --disable "${curl_args[@]}" "$users/$subject") || fail 'The private HTTPS admin endpoint could not be reached.'
if [[ "$status" != 200 ]] || ! jq -e --arg subject "$subject" '.id == $subject and .enabled == true' "$work/response" >/dev/null 2>&1; then
    fail 'The account lookup did not confirm the expected enabled account.'
fi
if [[ "$operation" == create ]]; then
    jq -e --arg expected "${account,,}" '(.username | ascii_downcase) == $expected' "$work/response" >/dev/null 2>&1 ||
        fail 'The account lookup did not confirm the newly created username.'
fi
printf '%s' "$subject" > "$work/subject"
(set -o noclobber; jq -n --arg issuer "$issuer" --rawfile subject "$work/subject" '{issuer:$issuer,subject:$subject}' > "$handoff") ||
    fail 'Could not write the new private handoff file.'
printf '%s\n' 'Private identity handoff written. Apply explicit Hosting grants separately.'
