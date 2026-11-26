-- Synthetic, throwaway local fixture roles. The preceding script installs
-- the real permission model; these logins are never used outside this stack.
ALTER ROLE archive_migrator PASSWORD 'rust235-migrator';
ALTER ROLE archive_runtime PASSWORD 'rust235-runtime';
