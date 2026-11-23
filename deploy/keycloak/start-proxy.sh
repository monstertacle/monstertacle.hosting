#!/bin/sh
set -eu
case "${KEYCLOAK_PUBLIC_ORIGIN:-}" in
    https://*) ;;
    *) exit 2 ;;
esac
# Only a literal HTTPS origin may enter Nginx configuration.
printf '%s\n' "$KEYCLOAK_PUBLIC_ORIGIN" |
    grep -Eqx 'https://[A-Za-z0-9.-]+(:[1-9][0-9]{0,4})?'
case "$KEYCLOAK_PUBLIC_ORIGIN" in
    *[!a-zA-Z0-9.:/-]*) exit 2 ;;
esac
umask 077
# shellcheck disable=SC2016
envsubst '${KEYCLOAK_PUBLIC_ORIGIN}' < /etc/nginx/hosting.conf.template > /tmp/hosting.conf
exec nginx -c /tmp/hosting.conf -g 'daemon off;'
