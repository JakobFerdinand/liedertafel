---
status: accepted
---

# The archive local stack and image gate run without Aspire

Decided with the maintainer on 2026-11-26, as part of the .NET-free archive
consolidation in #244. Aspire had been a deliberate earlier decision: the
AppHost (`src/archive/apphost/`, later the disposable image-test topology in
`src/archive/apphost-rust/`) orchestrated local development, and the Aspire
xUnit project (`tests/archive/apphost-rust/`) gated the production image.
The runtime cutover to Rust (#241) removed the orchestration's main occupant,
and #245/#246 built the replacements (`compose.yaml`, `dev.sh`, `dev.env` and
the shell gate `src/archive/backend-rs/tests/image/check-image-stack.sh`).
#247 now deletes the remaining Aspire projects and `src/archive/Archive.slnx`,
which is hard to reverse, so the reversal is recorded here.

## Decision

- **One runtime.** The archive stack runs on Compose plus shell scripts, not
  on .NET orchestration. `dev.sh up` starts PostgreSQL, Azurite, Mailpit and
  the OTLP collector; `dev.sh migrate`, `dev.sh worker <name>` and `dev.sh
  down` cover the explicit steps. The Rust API and the Next.js frontend start
  by hand with `dev.env` applied.
- **The shell gate owns the image check.** `check-image-stack.sh` boots the
  same throwaway dependency topology the AppHost used (PostgreSQL with the
  canonical role bootstrap and fixture passwords, Azurite, Mailpit, local OTLP
  collector on a private network) and verifies the cross-stack scenarios
  against the candidate image itself, with no host bind mounts.
- **No C# under the archive.** `src/archive/apphost-rust/`,
  `tests/archive/apphost-rust/` and `src/archive/Archive.slnx` are deleted.
  The frozen C# oracle stays reachable at commit
  `8c7a077988d3ca6a0a72788fb4cc13da9e3ae4a6`, and the frozen license file
  `src/archive/backend-rs/tests/oracles/dotnet-runtime-LICENSE.txt` is kept.
- **Repository-wide .NET stays for the Functions APIs.** `liedertafel.slnx`
  keeps the website/dashboard API projects, the shared pageview library and
  the dashboard API checks; `global.json` keeps pinning the SDK for them.
- **Renamed runtime knob.** The container-runtime variable of the image and
  translated gates is now `ARCHIVE_CONTAINER_RUNTIME` (was
  `ASPIRE_CONTAINER_RUNTIME`); CI and the gate documentation follow.

## Considered options

- *Keep the AppHost next to Compose for the image gate only*: rejected. Two
  orchestrations for one stack would keep the .NET SDK, the Aspire CLI bundle
  and a second startup path alive, and the AppHost duplicated the dependency
  topology that Compose now defines once.
- *Keep the Aspire xUnit project as the image gate*: rejected. Its value was
  the cross-stack assertions, not the runner; #246 translated them into the
  shell gate with the same fixtures and method-name mapping, so the .NET test
  infrastructure added version pinning without new coverage.
- *Keep `Archive.slnx` as an empty shell*: rejected. A solution file with no
  projects is dead weight and misleads readers into running dotnet against the
  archive.

## Consequences

- Local startup and verification are documented in `src/archive/README.md`
  and root `AGENTS.md`; the shell gate's primer is
  `src/archive/backend-rs/docs/image-apphost-parity.md`, which keeps the
  assertion-translation table and the frozen-oracle commit pointer.
- `src/archive/apphost-rust/fixture-role-passwords.sql` moved unchanged to
  `src/archive/backend-rs/tests/image/` so the gate keeps booting the same
  role model and fixture passwords.
- The shared synthetic fixtures stay: `tests/archive/apphost/TestAssets/`
  (video clip for the Rust and browser tests) and
  `tests/archive/fixtures/drive-inventur/` (bulk-import corpus, #143).
- Files named `...apphost...` (the image checks, the parity guide) keep their
  historical names; only their content describes the current gates.
