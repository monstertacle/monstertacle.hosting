#!/usr/bin/env bash
set +x
set -euo pipefail

KEYCLOAK_DB_PASSWORD=$(< /run/secrets/database_password)
if [[ ! "$KEYCLOAK_DB_PASSWORD" =~ ^[[:xdigit:]]{48,128}$ ]]; then
    printf '%s\n' 'Create a random hexadecimal Keycloak database password of 48 to 128 characters.' >&2
    exit 1
fi
export KEYCLOAK_DB_PASSWORD

psql --no-password --no-psqlrc --username postgres --dbname postgres --set ON_ERROR_STOP=1 <<'EOF_SQL'
SET log_min_error_statement = 'panic';
\getenv keycloak_password KEYCLOAK_DB_PASSWORD
CREATE ROLE keycloak LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD :'keycloak_password';
CREATE DATABASE keycloak OWNER keycloak;
REVOKE ALL ON DATABASE keycloak FROM PUBLIC;
EOF_SQL
