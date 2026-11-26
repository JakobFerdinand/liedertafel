# Production image and cross-stack shell gate (#235, #246)

`src/archive/Dockerfile` builds the normal locked release `archive-server`,
and Node 24 + pnpm 11.3.0 build the unchanged Next static export. The runtime
contains no Node or .NET; see [native inventory](image-native-notices.md).
Local tests do not validate ARM/musl or authorize a hosted release.

The cross-stack gate `tests/image/check-image-stack.sh` boots the inherited
image-test topology itself: fresh PostgreSQL 17.6, Azurite 3.35.0 and
Mailpit 1.27.8 containers plus a local OTLP collector, on a private per-run
network and without persistent resources. PostgreSQL receives the canonical
role bootstrap (`infrastructure/neon/bootstrap-roles.sql`) plus the synthetic
fixture passwords (`tests/image/fixture-role-passwords.sql`, relocated from the
deleted `src/archive/apphost-rust/` by #247) before first start; both files are
reused unchanged, and the separate adoption
database is created afterwards. The storage initializer, API, explicit migrate
and adoption check and the smoke worker run the same local tag with image
pulling disabled. The gate verifies only what is uniquely cross-stack: stack
boot, explicit migration, API liveness with the served static export, one
smoke worker pass and the adoption check. Rust suites and the Playwright
browser suite keep covering everything else.

In the private container network, `archive-blobs-235:10000` is an actual DNS
alias of Azurite, including for its server-side copy fetch. Every image
container reaches PostgreSQL, Azurite and Mailpit through that network, so no
host ports are published for dependencies and no host bind mounts are needed
(SQL reaches PostgreSQL through `cp` and `exec`, collector configuration
through the environment). The emulator's `--disableProductStyleUrl` selects
account-in-path addressing for this alias; `--skipApiVersionCheck` is for the
pinned local emulator and does not disable authentication. PostgreSQL runs the
canonical role bootstrap and synthetic test-only passwords. All email goes to
Mailpit, and the model is the product's offline scripted provider below the
real Gateway.

## Run

Requires Podman (or Docker) and Python 3. From the repo root:

```sh
export ARCHIVE_CONTAINER_RUNTIME=podman
export Archive__TestImageTag=local-$(git rev-parse --short HEAD)
export ARCHIVE_EXPECTED_IMAGE_VERSION=$Archive__TestImageTag
podman build --layers \
  --ignorefile src/archive/Dockerfile.dockerignore \
  -f src/archive/Dockerfile \
  --build-arg VERSION="$ARCHIVE_EXPECTED_IMAGE_VERSION" \
  -t "localhost/liedertafel-archive:$Archive__TestImageTag" .
bash src/archive/backend-rs/tests/image/check-apphost-image.sh
bash src/archive/backend-rs/tests/image/check-apphost-image-failures.sh
bash src/archive/backend-rs/tests/image/check-image-stack.sh
```

The gate asserts, in order: storage initialization exits 0; the API serves
`/alive`, `/system/status/` with a hashed `/_next` asset, `/api/build` with
`development: true` and the expected version, and a missing API route as a
JSON problem; the explicit `--migrate` exits 0 and leaves exactly the four
SQLx versions applied; `--worker-smoke` exits 0; and the separately frozen
adoption database migrates from the old EF head while preserving the synthetic
Lied (44 EF history rows, four successful SQLx versions). The explicit migration
is a contract: merely starting the API never applies a schema. The gate removes
its containers, network and captured logs on exit; do not publish logs even so,
because synthetic codes and loopback tokens in dependency logs are private
capture data. Independent
image checks bound the isolated codec run to 2 CPU and 512 MiB RAM on a
read-only root with 128 MiB tmpfs scratch, and measure actual scratch use. The
dependency-free origin control also uses 2 CPU and 512 MiB RAM, actual
liveness/static assets/build/404, observed stats and SIGTERM followed by exit 0.
Scratch owner/mode and writable-file permissions are tested; these synthetic
bounds are **not** a worker sizing recommendation. The gate's API and finite
image containers use a local envelope of 3 CPU and 1 GiB RAM. Even with
bounded spooling and finite drain, native PDF decode is not hard-preemptible;
large decoded PDFs/photos, hosted concurrency and long-video scratch use
retain their earlier capacity qualifications.

The separate failure controls use the exact image's shell with harmless
overrides for writable export, Node, unwanted/missing libraries, failed
`ldd`, and a failed linkage check. They do not modify the image.

From `src/archive/backend-rs`, the new OTLP test runs without containers:

```sh
cargo test --locked -p archive-telemetry --test http_protobuf \
  finite_jobs_flush_authenticated_http_protobuf_even_on_failure
# Also exercise the real image against the authenticated in-memory collector:
cargo test --locked -p archive-telemetry --test http_protobuf \
  actual_image_finite_jobs_flush_authenticated_http_protobuf -- --ignored
```

For the second command, pre-pull `docker.io/library/postgres:17.6` and
`docker.io/library/node:24-bookworm-slim`. The test restricts both to a
new internal container network, without published ports. It drives the product
`--migrate` success/failure against disposable PostgreSQL / an invalid synthetic
connection string and checks the exact exits, collector authentication, typed
protobuf resource/span/status/metric/log, and absence of credential/secret
markers in every payload and both console streams. The transport-only case
also uses a synthetic job and independent host capture; unlike frozen C#, Rust
keeps unsafe URL/exception data out of the SDK rather than attaching it and
scrubbing later. Existing gRPC default and transport remain unchanged.

## Assertion translation

The source oracle is `tests/archive/apphost` and its seven `[Fact]` methods
at `8c7a077988d3ca6a0a72788fb4cc13da9e3ae4a6` in git history. The Aspire xUnit
copies in `tests/archive/apphost-rust` retained the original method names,
fixtures and assertion bodies until #247 removed them together with the AppHost
topology; the shell gate above now carries the scenarios, and the table below
keeps the translation.

| Frozen method | Image assertion ownership |
| --- | --- |
| `CleanStartConnectsDependenciesAndRunsExplicitMigrationsAndWorker` | Real Next proxy/deep link/build; real database diagnostic before and after explicit migrate; positive and forged/missing CSRF exercise; same three roles, known/unknown parity, Mailpit sign-in/me/logout, five-way one-winner code consumption, invites/stable identity, role rules, revoke/reactivate, email change/collision, final-admin protection, explicit worker exit 0 and exact 11 mails. Historical EF pending suffixes are replaced by **exact four SQLx pending steps**, empty initial public schema, SHA-384 applied history, all 44 frozen identifiers as baseline metadata, current auth/events/Programme/recording/Lied/Datei schema, obsolete-entity absence, and real distinct-role grants/denials. Separate clean fixture DB restores independently frozen head + synthetic Lied and proves real SQLx adoption preserves it; no old EF transformations or fake pending names execute. |
| `PrivateScoreUploadRoundtripThroughRealAzuriteStorage` | Creator-only finalize, missing upload, actual PDF bytes and validation, idempotent finalize, anonymous/member roles, exact view/download bytes and disposition, immutable replacement and trash visibility. Only direct-Blob socket selection changes. |
| `MixedVoiceBatchRoundtripThroughRealAzuriteStorage` | Partitur, MIDI and voice audio, detected bytes and response metadata, replay IDs, member upload denial and actual signed bytes. Container (not ProjectResource) config selects the scripted product AI provider. Preserves actual chat SSE/citation assertions and real ledger deltas (+1 entry/+2 calls, positive settled cost, no reserve, one month row, EUR 15 cap). |
| `LargeUploadInterruptResumeRoundtripThroughRealAzuriteStorage` | Same 12 MiB across two blocks, interrupted staged upload, same pending identity, block-list discovery, ordered commit, SHA-256, replay/mismatch/cancel/size cap. Only direct-Blob socket selection changes. |
| `ConcertRecordingRoundtripThroughRealAzuriteStorage` | Original retention, refusal of false playback, real WebM byte/range/renewal/download path, unpublished visibility, Programme and Zeitmarken/history, cross-event/time/duplicate/replacement and stale guards, selected review restoration. Only direct-Blob socket selection changes. |
| `AVideoAufnahmeFinishesWithAPlayableH264CopyOnTheRealStack` | Same synthetic MPEG-4/AAC input, actual finite worker, completed distinct MP4 and exact original download, slot healing, publish and member copy. Additionally uses the image's own ffprobe and full A/V decode to prove H.264/AAC content. |
| `ExtractionRoundtripThroughRealAzuriteQueueAndFiniteWorker` | Real PDF→Blob→outbox→Queue→finite extraction, timestamps/single attempt, empty rerun, raster/no-text, duplicate/no-op, handoff to actual offline scripted reading plus prefill, lyrics/attribution and progress. Reruns require a **new timestamped `Extraction drain completed` log identity** and exit 0; an old Finished snapshot alone cannot pass. |

The broader #220 canonical schema/adoption drift/older-schema/permission
gates still stand. The release check does not rerun old EF transformations
or reclassify existing failed controls.

The original #235 audit snapshot recorded eight inherited findings. The
`lettre` 0.11.24 update clears `RUSTSEC-2026-0560` through `-0565`.
`RUSTSEC-2026-0543` (`chrono`) and `RUSTSEC-2026-0555` (`chrono-tz`) remain
visible, but only those two **unmaintained** findings are nonblocking per
[#236 comment 7011201805](https://github.com/JakobFerdinand/liedertafel/issues/236#issuecomment-7011201805).
`tests/image/check-dependencies.sh` keeps raw findings and fails on other errors.
Removal remains follow-up; local test passes do not authorize cutover.
