#!/usr/bin/env bash
set +x
set -euo pipefail

KC_DB_PASSWORD=$(< /run/secrets/database_password)
[[ -n "$KC_DB_PASSWORD" ]] || exit 1
export KC_DB_PASSWORD

if [[ -f /run/secrets/bootstrap_password ]]; then
    KC_BOOTSTRAP_ADMIN_PASSWORD=$(< /run/secrets/bootstrap_password)
    [[ -n "$KC_BOOTSTRAP_ADMIN_PASSWORD" ]] || exit 1
    export KC_BOOTSTRAP_ADMIN_PASSWORD
fi

exec /opt/keycloak/bin/kc.sh start --import-realm
