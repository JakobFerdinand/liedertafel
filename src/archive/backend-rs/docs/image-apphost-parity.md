# Rust image against the Aspire cross-stack oracle (#235)

Local-only. `Dockerfile.apphost` builds the normal locked release
`archive-server`, and Node 24 + pnpm 11.3.0 build the unchanged Next static
export. The runtime contains no Node or .NET; see
[native inventory](image-native-notices.md). This is not a production
Dockerfile, registry image, ARM/musl test, or authorization for #236.

`src/archive/apphost-rust` creates new PostgreSQL 17.6, Azurite 3.35.0 and
Mailpit 1.27.8 containers, without persistent resources. Aspire 13.6.5 is
used **only** in the additive gate (13.5.3 in the frozen stack has
GHSA-f7vh-h5g3-7jrh). Aspire injects its real dashboard OTLP/HTTP endpoint.
The live API, storage initializer, explicit migrate/adoption checks and four
finite workers use the same local tag with image pulling disabled. The
unchanged Next dev server still proxies real API traffic; we separately fetch
the deep link and a hashed JS asset **from the image**, compare the build stamp
through both origins, and assert a missing API route is a JSON problem.

Azurite uses separate dynamically mapped host sockets. In the private
container network, `archive-blobs-235:10000` is an actual DNS alias of Azurite,
including for its server-side copy fetch. The xUnit transfer client maps only
that destination's **socket** to Aspire's allocated local port. It never
changes the signed path, query or Host, and refuses external destinations.
The emulator's `--disableProductStyleUrl` selects account-in-path addressing
for this alias; `--skipApiVersionCheck` is for the pinned local emulator
and does not disable authentication. PostgreSQL runs the canonical role
bootstrap and synthetic test-only passwords. All email goes to Mailpit, and
the model is the product's offline scripted provider below the real Gateway.

## Run

Requires rootless Podman (or local Docker), .NET 10, Node 24, pnpm, Python 3,
and a worktree with #234's diagnostics/finite operations. Do not share a
frontend worktree with another dev server. From the repo root:

```sh
export ASPIRE_CONTAINER_RUNTIME=podman
export Archive__TestImageTag=local-$(git rev-parse --short HEAD)
export ARCHIVE_235_EXPECTED_VERSION=$Archive__TestImageTag
podman build --layers \
  --ignorefile src/archive/backend-rs/Dockerfile.apphost.dockerignore \
  -f src/archive/backend-rs/Dockerfile.apphost \
  --build-arg VERSION="$ARCHIVE_235_EXPECTED_VERSION" \
  -t "localhost/liedertafel-235:$Archive__TestImageTag" .
bash src/archive/backend-rs/tests/image/check-apphost-image.sh
pnpm --dir src/archive/frontend install --frozen-lockfile
dotnet test tests/archive/apphost-rust
# Focused equivalent, from the root:
dotnet test tests/archive/apphost-rust --filter FullyQualifiedName~ExtractionRoundtripThroughRealAzuriteQueueAndFiniteWorker
```

Use the tests' normal teardown or allow the cancellation timeout to unwind.
Do not publish logs: even synthetic codes, loopback dashboard sign-in tokens
and SAS query values in dependency logs are private capture data. Independent
image checks bound the isolated codec run to 2 CPU and 512 MiB RAM on a
read-only root with 128 MiB tmpfs scratch, and measure actual scratch use. The
dependency-free origin control also uses 2 CPU and 512 MiB RAM, actual
liveness/static assets/build/404, observed stats and SIGTERM followed by exit 0.
Scratch owner/mode and writable-file permissions are tested; these synthetic
bounds are **not** a worker sizing recommendation. AppHost API and finite
containers use a local envelope of 3 CPU and 1 GiB RAM. Even with
bounded spooling and finite drain, native PDF decode is not hard-preemptible;
large decoded PDFs/photos, hosted concurrency and long-video scratch use
retain their earlier capacity qualifications.

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

The source of truth is `tests/archive/apphost` and its seven `[Fact]` methods.
The copies in `tests/archive/apphost-rust` deliberately retain original
method names, fixtures and assertion bodies, so a side-by-side diff shows
each topology and oracle translation.

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
gates still stand. This gate does not retire frozen source, excused historical
EF transformations, wider browser suites, or any existing failed controls.
