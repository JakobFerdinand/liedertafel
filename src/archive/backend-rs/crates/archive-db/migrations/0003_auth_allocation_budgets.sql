-- A fixed-size ledger, never an attacker-grown per-address table. Slots are
-- reused after ten minutes; addresses are represented only by SHA256 digests.
CREATE TABLE archive_auth_allocation_budgets (
    slot smallint PRIMARY KEY CHECK (slot >= 0 AND slot < 1024),
    address_digest bytea UNIQUE CHECK (octet_length(address_digest) = 32),
    allocations smallint NOT NULL DEFAULT 0 CHECK (allocations >= 0 AND allocations <= 30),
    expires_at timestamptz NOT NULL DEFAULT '-infinity',
    CHECK ((address_digest IS NULL) = (allocations = 0))
);
INSERT INTO archive_auth_allocation_budgets (slot)
SELECT generate_series(0, 1023);
CREATE INDEX archive_auth_allocation_budgets_expiry ON archive_auth_allocation_budgets(expires_at);
