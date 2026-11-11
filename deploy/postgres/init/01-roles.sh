#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d), as the superuser.
# Everything that needs superuser rights happens here; everything else is a migration run as saas_migrator.
#
#   saas_migrator       owns the app, admin and audit schemas and every table in them; runs migrations (DDL).
#                       Table owners bypass RLS unless FORCE ROW LEVEL SECURITY is set, and can switch RLS off, so the
#                       application must never connect as this role.
#   saas_app            what the API connects as. NOSUPERUSER, NOBYPASSRLS, not the owner of anything: every row it
#                       reads or writes goes through the tenant policies.
#   saas_admin          platform operators' connection. No table privileges at all; it may only EXECUTE the audited
#                       functions in the admin schema.
#   saas_admin_definer  NOLOGIN, BYPASSRLS. Owns the SECURITY DEFINER admin functions, which is the only way
#                       cross-tenant work happens, and each of them writes an audit row before it returns.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"
: "${ADMIN_PASSWORD:?ADMIN_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v admin_password="$ADMIN_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE saas_migrator LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD :'migrator_password';
CREATE ROLE saas_app LOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT PASSWORD :'app_password';
CREATE ROLE saas_admin LOGIN NOSUPERUSER NOBYPASSRLS NOINHERIT PASSWORD :'admin_password';
CREATE ROLE saas_admin_definer NOLOGIN NOSUPERUSER BYPASSRLS;

-- The migrator creates the admin functions *as* saas_admin_definer (SET ROLE) so the definer owns them. INHERIT FALSE:
-- the migrator does not silently carry the definer's privileges into every other statement it runs.
GRANT saas_admin_definer TO saas_migrator WITH INHERIT FALSE, SET TRUE;

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO saas_migrator, saas_app, saas_admin;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA app AUTHORIZATION saas_migrator;
CREATE SCHEMA admin AUTHORIZATION saas_migrator;
CREATE SCHEMA audit AUTHORIZATION saas_migrator;

-- Functions are executable by PUBLIC by default. Nothing the migrator creates should be callable by a role that was
-- not explicitly granted it, least of all a SECURITY DEFINER function owned by a BYPASSRLS role.
ALTER DEFAULT PRIVILEGES FOR ROLE saas_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE saas_admin_definer REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
SQL
