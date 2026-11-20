# Frozen Playwright selector: actual .NET baseline failure

Verified locally on 2026-11-09, not inferred from a Rust failure or source alone.
No frozen frontend, test, C# host, or C# test source was edited.

## Controlled comparison

- Frozen source point: `0ff95e6`. C# backend tree:
  `895c9885aec1db0b8d2bc8150037b1c8d0e37ea9`; frontend tree:
  `8725775a07ccab4eda9c2ae982d2ace819b42dc3`.
- The **unchanged** `src/archive/frontend/tests/passkey.spec.ts` has SHA256
  `eaf29d8641b62bcdcb8f45f60fa2eb486251ef65d5e53261c53ce18c0a6ff283`, matching
  `git show 0ff95e6:src/archive/frontend/tests/passkey.spec.ts` byte for byte.
- The frontend was exported from that source in a separate temporary directory;
  `pnpm install --frozen-lockfile` and `pnpm run build` passed. The static build
  was freshly repeated after the verifier change, without touching frontend/.
- A native **.NET 10 host** ran the frozen backend with Development enabled,
  its own disposable PostgreSQL database, actual EF migrations, local Mailpit,
  and a local gRPC OTLP receiver. Reading was explicitly disabled. This was not
  a Rust server or mocked login. The first migration attempt applied the schema
  but exited 1 because no OTLP receiver was listening; after starting the local
  receiver, the explicit migration rerun exited 0. No failed migration command
  is counted as passing.
- `ASPNETCORE_WEBROOT` for .NET and `ARCHIVE_STATIC_ROOT` for both Rust origins
  pointed to the **same export directory**. HTTP GET `/archiv/` from all three
  origins was byte-identical to the file, SHA256
  `5c2970d9d38849560874689a4aefe4c3137f90ac974011ddf2b0e82894512923`.
  Export hashes can differ between builds because of generated build IDs; this
  comparison was made against the actual shared export used in the final runs.

## Actual failure on both origins

The Rust-owned Playwright config points at the original frontend test file;
it does not copy, rewrite, skip, or replace its assertions. Run from repo root,
with `NODE_PATH` pointing at the external test-only Playwright install:

```bash
# Native frozen .NET on localhost:58621, local Mailpit on 127.0.0.1:58021.
ARCHIVE_BASE_URL=http://localhost:58621 \
ARCHIVE_MAIL_URL=http://127.0.0.1:58021 \
ARCHIVE_CHROMIUM=/path/to/chromium \
  node /external/node_modules/@playwright/test/cli.js test \
  --config src/archive/backend-rs/tests/browser/playwright.config.cjs
# Repeat the same command with the Rust base URL (localhost:58421).
```

Both real runs executed **1 test, 0 skipped**, exited **1**, and failed at the
same original line **102**, after the seed, email/code exchange and line-101
`/archiv/` navigation assertion had succeeded:

```text
Error: expect(locator).toBeVisible() failed
Locator: getByText('Willkommen im Archiv.')
Expected: visible
Timeout: 5000ms
Error: element(s) not found
  101 | await expect(page).toHaveURL(/\/archiv\//);
> 102 | await expect(page.getByText("Willkommen im Archiv.")).toBeVisible();
1 failed
```

The .NET failure's captured accessible page state contains:

```text
region "Dein Konto"
heading "Dein Konto" [level=2]
paragraph: Angemeldet als Testmitglied (Member)
heading "Passkeys dieses Kontos" [level=3]
paragraph: Noch kein Passkey registriert.
textbox "Name für den neuen Passkey"
button "Passkey hinzufügen"
```

This is a **proven baseline test/UI mismatch**, not a passing frozen test.
The later line-151 substring locator can also match the empty-state text, but
that is a source-level concern: this unmodified run never reaches line 151,
and no second observed failure is claimed.

## Independent positive controls, not substitutes for the frozen result

`tests/browser/ceremonies.cjs enroll` passed against the **native .NET host**
serving the same freshly rebuilt static UI: email → real Chromium enrollment →
logout → discoverable passkey login, with zero page errors. The same Rust-owned
harness passed against the 0.5.5/OpenSSL Rust origin, and its `legacy` mode
authenticated the actual synthetic .NET-generated row against a fresh Rust
challenge. The positive controls assert current UI text and exact enrollment
success; they neither modify nor reclassify the frozen failure. The fixture was
not regenerated or overwritten during this comparison.

Local evidence files (outside the checkout):
`/tmp/opencode/221-frozen-playwright-dotnet.log`,
`/tmp/opencode/221-frozen-playwright-rust-055.log`,
`/tmp/opencode/221-static-parity-final.log`, and
`/tmp/opencode/221-browser-dotnet-positive-control-final.log`.
The Playwright error context is under
`/tmp/opencode/221-browser-dotnet-baseline/`. All accounts/credentials/mail in
these runs were synthetic and local; no production, ACS, AI or Neon access was
used. There was no source contradiction preventing the .NET comparison.
