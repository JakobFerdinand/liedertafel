-- Run as archive_migrator after each successful explicit EF migration, before
-- starting/releasing the runtime. Keep EF bookkeeping inaccessible to the app.
\set ON_ERROR_STOP on
BEGIN;
DO $$ BEGIN
    IF current_user <> 'archive_migrator' OR current_database() <> 'archive' THEN
        RAISE EXCEPTION 'Run grants as archive_migrator on database archive';
    END IF;
END $$;
-- Execute as the object creator. Neon admin cannot change these defaults on
-- behalf of a SQL-created role without an additional membership grant.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO archive_runtime;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO archive_runtime;
ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO archive_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO archive_runtime;
REVOKE ALL ON TABLE public."__EFMigrationsHistory" FROM archive_runtime;
COMMIT;
