-- Identity users/passkeys/codes remain unchanged. Only Rust-owned session state.
CREATE TABLE archive_sessions (
    token_digest bytea PRIMARY KEY CHECK (octet_length(token_digest) = 32),
    user_id uuid NOT NULL REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    security_stamp text,
    verified_at timestamptz NOT NULL,
    auth_method text NOT NULL CHECK (auth_method IN ('email_code', 'passkey')),
    renewed_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL
);
CREATE INDEX archive_sessions_user ON archive_sessions(user_id);
CREATE INDEX archive_sessions_expiry ON archive_sessions(expires_at);

CREATE TABLE archive_csrf (
    browser_digest bytea PRIMARY KEY CHECK (octet_length(browser_digest) = 32),
    request_token text NOT NULL CHECK (length(request_token) = 43),
    session_digest bytea REFERENCES archive_sessions(token_digest) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL
);
CREATE INDEX archive_csrf_expiry ON archive_csrf(expires_at);

CREATE TABLE archive_ceremonies (
    browser_digest bytea NOT NULL CHECK (octet_length(browser_digest) = 32),
    operation text NOT NULL CHECK (operation IN ('register', 'login')),
    session_digest bytea REFERENCES archive_sessions(token_digest) ON DELETE CASCADE,
    user_id uuid REFERENCES "AspNetUsers"("Id") ON DELETE CASCADE,
    state jsonb NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (browser_digest, operation),
    CHECK ((operation = 'register') = (session_digest IS NOT NULL AND user_id IS NOT NULL))
);
CREATE INDEX archive_ceremonies_expiry ON archive_ceremonies(expires_at);
