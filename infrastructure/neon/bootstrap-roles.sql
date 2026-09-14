-- One-time bootstrap on a new, empty archive database, as archive_admin.
-- Set passwords interactively with psql \password after this transaction.
-- Existing role names deliberately fail rather than silently trusting their grants.
\set ON_ERROR_STOP on
BEGIN;
DO $$ BEGIN
    IF current_user <> 'archive_admin' OR current_database() <> 'archive' THEN
        RAISE EXCEPTION 'Run bootstrap as archive_admin on database archive';
    END IF;
END $$;

CREATE ROLE archive_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
    NOREPLICATION NOBYPASSRLS;
CREATE ROLE archive_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE
    NOREPLICATION NOBYPASSRLS;

REVOKE ALL ON DATABASE archive FROM PUBLIC;
GRANT CONNECT ON DATABASE archive TO archive_migrator, archive_runtime;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO archive_migrator;
GRANT USAGE ON SCHEMA public TO archive_runtime;

COMMIT;
