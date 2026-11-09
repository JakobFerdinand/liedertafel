# Rust-Port Agent Setup (#218/#219)

Status: research findings for preparing the agent tooling around `backend-rs`
(wave 1, ticket [#219](https://github.com/JakobFerdinand/liedertafel/issues/219),
parent spec [#218](https://github.com/JakobFerdinand/liedertafel/issues/218)).
Researched 2026-11-07 against primary sources only; all URLs were verified live
on that date. Nothing here runs builds — it only decides what the repo teaches
the agent and which skills to vendor.

## 1. AGENTS.md best practices

### 1.1 The convention

- AGENTS.md is a plain-Markdown convention ("README for agents") with no
  required fields and no schema; agents simply parse the text
  ([agents.md](https://agents.md/)). It emerged in August 2025 from OpenAI
  Codex, Amp, Jules, Cursor and Factory, was adopted by 60k+ open-source
  projects, and in December 2025 was donated to the Agentic AI Foundation
  under the Linux Foundation alongside MCP and Goose
  ([history summary](https://addyosmani.com/agents/15-agents-md),
  [governance summary](https://s-celles.github.io/ai-config/docs/agents-md)).
- Nested files are part of the convention: "place another AGENTS.md inside
  each package. Agents automatically read the nearest file in the directory
  tree, so the closest one takes precedence" ([agents.md FAQ](https://agents.md/)).
  The "closest file wins" rule is documented but not formally specified —
  the spec is intentionally thin
  ([codeline review, 2025-10-25](https://www.codeline.co/thoughts/repo-review/2025/agents-md-open-format-for-guiding-coding-agents)).
- Explicit user prompts always override AGENTS.md content
  ([agents.md FAQ](https://agents.md/)).

### 1.2 How OpenCode loads it (the tool this repo actually runs)

OpenCode's rules docs: it looks first for `AGENTS.md` (then `CLAUDE.md` as
fallback), mixes in the global `~/.config/opencode/AGENTS.md`, and supports
extra instruction files via the `instructions` array in `opencode.json`
([rules](https://opencode.ai/docs/rules/)). The current V2 behavior is
narrower and important for us:
([V2 instructions](https://v2.opencode.ai/docs/instructions/)):

- The initial context contains the global file plus project `AGENTS.md` files
  from the working location upward to the repo root. `CLAUDE.md` fallbacks
  are gone in V2.
- A nested `AGENTS.md` below the working location is **not** loaded up front —
  it is discovered when the agent reads a file or lists a directory in that
  subtree, and injected nearest-first, once per session. So a
  `src/archive/backend-rs/AGENTS.md` reliably loads the first time the agent
  touches backend-rs files, even in a session started at the repo root.
- OpenCode does not resolve conflicts between files (V1: first match wins;
  V2: additive). Keep the nested file self-consistent with the root file and
  never contradicting it.

### 1.3 What to write (and not write)

Converging recommendations from agents.md, Factory, Anthropic and the
HumanLayer write-up:

Do:
- Keep it short. Common guidance caps it around 150 lines (AddyOsmani) and
  keeps combined instruction chains well under 32 KiB
  ([AGENTS.md guide](https://aicoding.club/docs/tutorials/agents-md-guide)).
  Root file covers shared practices; nested files hold only what is unique to
  a subdirectory (AddyOsmani). The repo's root AGENTS.md is already long — a
  second, focused file stays within budget.
- Give exact commands, not adjectives. "Bad: 'We use a modern JavaScript
  stack'. Good: 'React 18 with TypeScript 5.3, Vite 5'" (AddyOsmani). Factory
  shows the same contrast — vague "Be careful" vs. concrete test invocations
  ([Factory AGENTS.md docs](https://docs.factory.ai/cli/configuration/agents-md)).
- List runnable checks: agents will attempt to execute programmatic checks
  found in AGENTS.md and fix failures before finishing
  ([agents.md FAQ](https://agents.md/)).
- Prefer file:line paths to real docs over copying code snippets — snippets go
  stale fastest ([HumanLayer: Writing a good CLAUDE.md](https://www.humanlayer.dev/blog/writing-a-good-claude-md),
  which explicitly also applies to AGENTS.md).
- Never send an LLM to do a linter's job; encode rules as deterministic
  formatter/linter passes (HumanLayer).
- Treat it as living documentation and update it in the same PR when build or
  test conventions change; stale instructions actively mislead (AddyOsmani;
  Anthropic's Claude Code best practices make the same "keep it concise and
  iterate" point — [claude-code-best-practices](https://www.anthropic.com/engineering/claude-code-best-practices)).
- Factory's "what to avoid" list transfers 1:1: no secrets, no full copies of
  long docs, no stale directory inventories, no personal preferences that
  conflict with project rules, no temporary task notes, and no instructions to
  skip validation ([Factory](https://docs.factory.ai/cli/configuration/agents-md)).
- Skills vs. instructions: Factory's rule of thumb — "If guidance is useful
  for one workflow, make it a skill. If it is useful for one project area,
  put it in a nested AGENTS.md." That separation matches Anthropic's framing
  of skills as procedural, tool-focused knowledge
  ([equipping-agents-for-the-real-world-with-agent-skills](https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills)).

Don't:
- Don't paste long Rust how-tos into AGENTS.md — that is what skills and the
  Context7 skill are for (§2, §4).
- Don't hardcode crate versions or API examples that will drift (see §3).
- Don't invent rules that a tool already enforces (rustfmt/clippy cover style).

### 1.4 Recommended outline for `src/archive/backend-rs/AGENTS.md`

Roughly 80–120 lines, English, tab-indented code blocks, matching the tone of
the root AGENTS.md. Suggested section order:

```markdown
# backend-rs AGENTS.md          # 1–2 lines: what this crate is, ticket #218/#219 links
## Boundary                     # C# src/archive/backend is frozen; Rust code only in
                                # backend-rs; no cross-edits without an explicit ticket
## Workspace layout             # member crates + one-line purpose each (keep fresh!)
## Build, check, test           # ---
                                #   cargo build --workspace --locked -p <member>  (one job at a time;
                                #     parallelism rules live in the global instructions)
                                #   cargo test <member> ; focused: cargo test <path>
                                #   pnpm-deps free; frontend/ untouched
## Lint and format before you commit
                                # cargo fmt --all --check ; cargo clippy --workspace --all-targets
                                # lints live in workspace [lints] + clippy.toml — never
                                # inline #[allow] without a comment naming the ticket
## Toolchain                    # rust-toolchain.toml pins an exact stable patch; never
                                # bump it in an unrelated PR; Cargo.lock is committed, use --locked
## Documentation lookup is mandatory
                                # Before using any crate API from memory, look it up:
                                #   npx ctx7@latest library <Crate> "<specific query>"  (find-docs skill)
                                # docs.rs is the API reference of record:
                                #   https://docs.rs/<crate>
                                # When touching a crate version in Cargo.toml, re-read its
                                # changelog/release notes for that version before porting code.
## Known traps                  # short list, e.g.:
                                #   - Official Azure crates are azure_storage_blob /
                                #     azure_storage_queue (singular). azure_storage_blobs(_queues)
                                #     0.21.0 are dead legacy crates — never depend on them.
                                #   - azure_identity has no DefaultAzureCredential; use
                                #     DeveloperToolsCredential (local) / ManagedIdentityCredential (prod).
                                #   - axum 0.8 uses /{id}, not /:id; body-consuming extractors go last.
## Routes and contracts         # unported routes answer stable JSON 404/501; /api/build +
                                # readiness must exist from wave 1; link docs/architecture.md
## Done means                   # fmt+clippy+tests green, telemetry visible in the local stack,
                                # AGENTS.md updated in the same PR if conventions changed
```

Upkeep rule (belongs in the root AGENTS.md, not the nested one): when a PR
changes the workspace member list, build commands or CI checks, the same PR
edits the nested AGENTS.md — the file is small, so drift is cheap to fix.

## 2. Recommended skills & tooling for the Rust stack

### 2.1 Skill format and how this repo installs them

A skill is a folder with a `SKILL.md` (YAML frontmatter `name`/`description`
plus Markdown body, optional scripts/references beside it); the Agent Skills
standard is described by Anthropic and agentskills.io
([Anthropic engineering](https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills),
[anthropics/skills README](https://github.com/anthropics/skills)). OpenCode
discovers project skills in `.agents/skills` (compatibility location) and
`.opencode/skills` — this repo's `.agents/skills/` layout is already a valid
OpenCode skill source ([OpenCode skills docs](https://opencode.ai/docs/skills/)).
The agent loads a skill by ID; `description` drives discovery, and
`disable-model-invocation: true` keeps a skill out of the auto-list. Skill IDs
are path-derived, so a vendored folder `azure-storage-blob-rust/` plus its
`SKILL.md` frontmatter name should agree.

### 2.2 Existing skills found (as of 2026-11-07)

| Skill | Source | Why it fits | Caveats |
|---|---|---|---|
| `azure-identity-rust`, `azure-storage-blob-rust`, `azure-eventhub-rust` (+ keyvault/cosmos siblings) | [microsoft/skills](https://github.com/microsoft/skills), plugin `azure-sdk-rust` ([SKILL.md example](https://github.com/microsoft/skills/blob/main/.github/plugins/azure-sdk-rust/skills/azure-identity-rust/SKILL.md)), MIT, added Jan 2026, also listed on [skills.sh](https://www.skills.sh/microsoft/skills/azure-storage-blob-rust) | First-party (Microsoft) per-crate knowledge for exactly our Azure crates. Explicitly corrects two traps we would otherwise hit: only the official Crates published as `azure_storage_blob`/`azure_storage_queue` (singular), **not** the old 0.21.0 `azure_storage_blobs`; and `azure_identity` has no `DefaultAzureCredential` — use `DeveloperToolsCredential`/`ManagedIdentityCredential` | No queue SDK skill in the Rust set (blob/eventhubs only); queues will need our own notes or Context7 |
| `axum` | [terminalskills/skills](https://github.com/terminalskills/skills) ([registry page](https://skills.sh/terminalskills/skills/axum)), MIT, v1.1.0 updated 2026-10-29, community trust tier 2 | Covers axum 0.8 routing/`{id}` syntax, extractor ordering (body-consuming extractor must be last), `#[axum::debug_handler]`, sqlx-prepare offline builds — matches our axum/tower-http layer | Community skill; review body before vendoring; version pins inside community goods are the usual drift source |
| `rust` (baseline idiom) | e.g. [ericrisco/rsc-harness](https://www.skillsdirectory.com/skills/ericrisco-rust) (security grade A, targets Rust 1.85+/edition 2024, axum 0.8, tokio, sqlx, cargo-audit) or [apollographql/rust-best-practices](https://github.com/apollographql/skills) (company handbook) | Baseline ownership/error-handling/style guidance the agent reads before writing Rust | Many marketplace Rust skills are generic; prefer one and trim it |
| `webapp-testing` | [anthropics/skills](https://github.com/anthropics/skills) (official repo; no Rust skills exist there — its current skill list is doc/UI/MCP oriented) | Playwright-driven browser checks; the archive frontend already has Playwright browser tests and backend-rs must serve them | Not Rust-specific |

Anti-recommendation: most marketplace Rust skills pin dependency versions in
their bodies and go stale (e.g. the widely copied `samuel-axum` skill pins
axum 0.7/sqlx 0.7 [sample](https://github.com/samuelpkg/samuel-axum/blob/main/SKILL.md)).
Skills that hardcode versions should be vendored **without** the version
blocks, with AGENTS.md pointing at fresh lookup (§3). Registries themselves
warn to review skill instructions before use
([claude-plugins.dev axum page](https://claude-plugins.dev/skills/@TerminalSkills/skills/axum)).

### 2.3 Tooling the backend-rs AGENTS.md should teach

| Tool | Role | Primary source |
|---|---|---|
| `rustup` toolchain file | `rust-toolchain.toml` with an **exact patch version** (not `channel = "stable"`, which floats) pins compiler + components for dev and CI; commit it and Cargo.lock together ([rustup overrides](https://rust-lang.github.io/rustup/overrides.html), [pinning practice write-up, 2026-10-19](https://safeguard.sh/resources/blog/pin-the-rust-toolchain-with-rust-toolchain-toml)) | `channel = "1.99.1"` is current stable as of 2026-11-07 (verified via [channel-rust-stable.toml](https://static.rust-lang.org/dist/channel-rust-stable.toml)) |
| Edition 2024 | Stable since Rust 1.85 (Feb 2025) — the baseline for new code; note MSRVs of deps now typically ≥ 1.85 ([1.85 release](https://blog.rust-lang.org/2025/02/20/Rust-1.85.0/); Azure crate MSRV was raised to 1.88 in Feb 2026 ([Azure SDK Feb 2026 Rust release](https://azure.github.io/azure-sdk/releases/2026-02/rust.html))) | — |
| Clippy | Project-level lints via the repeating `[lints]` table in `Cargo.toml` (applies workspace-wide) plus a `clippy.toml` for option lints; footgun: `channel = "stable"` floats across six-week cycles and silently changes lints ([Clippy configuration](https://doc.rust-lang.org/clippy/configuration.html), [pinning write-up](https://safeguard.sh/resources/blog/pin-the-rust-toolchain-with-rust-toolchain-toml)) | — |
| rustfmt | `cargo fmt --all --check` as the first, cheapest premium check ([rustfmt](https://rust-lang.github.io/rustfmt/)) | — |
| rust-analyzer | Editors/agents navigate via rust-analyzer; its manual is the reference for settings ([manual](https://rust-analyzer.github.io/manual.html)) | — |
| cargo-nextest | Faster, leaner test runner — scales process count down cleanly on small hosts ([nexte.st](https://nexte.st)) | — |
| `cargo-deny` / `cargo-audit` | Advisory + license + duplicate-crate checks; `cargo-deny` covers all three in one `deny.toml`; pure `cargo-audit` is the lighter alternative ([cargo-deny](https://embarkstudios.github.io/cargo-deny/), [RustSec](https://github.com/rustsec/rustsec)) | — |
| `cargo doc` (rustdoc) | Documentation is the API of record; doctests are compiled and run by `cargo test`, so examples stay honest ([rustdoc documentation tests](https://doc.rust-lang.org/rustdoc/write-documentation/documentation-tests.html)) | — |
| `sqlx` offline data | `cargo sqlx prepare` commits `.sqlx/` so compile-time-checked queries build without a DB in CI ([sqlx repo](https://github.com/launchbadge/sqlx)) | — |

## 3. Version freshness mechanics

### 3.1 The repo's existing Context7 skill

`.agents/skills/find-docs/` wraps `npx ctx7@latest library <name> "<query>"` →
`npx ctx7@latest docs <libraryId> "<query>"`, max 3 calls per question, and
tells the agent: "Use even when you think you know the answer — do not rely
on training data… do not silently fall back to training data"
([SKILL.md](../../.agents/skills/find-docs/SKILL.md)). It is library-agnostic,
so it already covers tokio/axum/sqlx/tracing/Azure crates — nothing new to
install for coverage; the problem is making it *trigger* for Rust too. The
frontmatter description names only web-dev examples (React, Next.js, Prisma,
Express, Tailwind, Django, Spring Boot), which visibly biases the trigger
toward the website/dashboard. Fix: strengthen the AGENTS.md instruction
(§4) and optionally add a Rust words list ("tokio", "axum", "tower-http",
"sqlx", "azure_identity", "pdfium") to the description so it fires on
backend work.

### 3.2 llms.txt inventory (probed 2026-11-07, HTTP status)

The [llmstxt.org](https://llmstxt.org/) convention (v1 Sep 2024, v2 modified
2026-08-10) is a `/llms.txt` index plus markdown page versions; documentation
platforms generate them automatically and Lighthouse even audits for one.
In the Rust crate ecosystem it is largely absent:

| Site | llms.txt? | Evidence |
|---|---|---|
| opentelemetry.io | **Yes** | `https://opentelemetry.io/llms.txt` returns 200 with a full index; pages have `.md` versions ([llms.txt](https://opentelemetry.io/llms.txt)) |
| tokio.rs | No | `/llms.txt` → 404 |
| axum.rs / axum project site | No | 404 (axum API docs live on docs.rs anyway) |
| docs.rs (per crate) | No | `/llms.txt`, `/axum/llms.txt`, `/axum/latest/axum/llms.txt` → 400/404; docs.rs serves classic rustdoc HTML (e.g. `/all.html`) |
| lettre.rs | No | 404 |
| rustup docs | No | 404 |
| sqlx | No | project sites unreachable/404; authoritative sources remain [docs.rs](https://docs.rs/sqlx) and the [repo README](https://github.com/launchbadge/sqlx) |

Practical consequence: for most of our crates there is no llms.txt shortcut;
the working mirrors are (a) Context7 via find-docs, (b) docs.rs directly, and
(c) crates.io release pages. OpenTelemetry is the pleasant exception — its
markdown pages are perfect agent fodder for instrumenting the OTLP layer.

### 3.3 crates.io / docs.rs as authoritative version source

The crates.io API answers `max_stable_version` + `updated_at` per crate; each
crate's docs.rs URL is the API reference of record (§2.3). Values below were
pulled from the crates.io API on **2026-11-07**; treat them as a starting
point and re-verify at implementation time:

| Crate | Latest stable (2026-11-07) | Note |
|---|---|---|
| tokio | 1.54.1 (2026-10-21) | runtime baseline |
| axum | 0.8.9 (2026-04-14) | 0.8.x line; `/{id}` path syntax, no `#[async_trait]` ([axum 0.8 announcement](https://tokio.rs/blog/2025-01-01-announcing-axum-0-8-0)) |
| tower-http | 0.7.1 (2026-08-31) | static files + compression layers |
| sqlx | 0.9.0 (2026-05-21) | watch changelog vs. 0.8 assumptions |
| tracing / tracing-subscriber | 0.1.44 / 0.3.23 | — |
| tracing-opentelemetry | 0.34.0 (2026-09-23) | pairs with OTel SDK |
| opentelemetry / opentelemetry-otlp | 0.33.1 / 0.33.1 (2026-10-09) | verify against [opentelemetry-rust](https://github.com/open-telemetry/opentelemetry-rust) |
| reqwest | 0.13.5 (2026-09-08) | hand-rolled AOAI client; SSE support |
| clap | 4.6.7 (2026-09-14) | CLI surface |
| lettre | 0.11.23 (2026-08-03) | mail |
| webauthn-rs | 0.6.2 (2026-10-31) | passkeys ([repo](https://github.com/kanidm/webauthn-rs)) |
| pdfium-render | 0.9.5 (2026-11-01) | needs bundled PDFium native lib ([repo](https://github.com/ajrcarey/pdfium-render)) |
| azure_identity | 1.0.0 | GA |
| **azure_storage_blob** (singular!) | 1.1.0 | May 2026 GA renamed away from the plural preview generations ([Azure SDK May 2026 releases](https://azure.github.io/azure-sdk/releases/2026-05/rust.html), [GA announcement](https://devblogs.microsoft.com/azure-sdk/azure-sdk-release-may-2026)) |
| **azure_storage_queue** (singular!) | 1.1.0 | same rename ([Azure latest-releases table](https://azure.github.io/azure-sdk/releases/latest/rust.html)) |

The single most dangerous version trap found: **`azure_storage_blobs` /
`azure_storage_queues` (plural, 0.21.0, last released 2024-10-15) are the
dead legacy track-1 crates.** The plural names rank higher in casual
searches and marketplaces, and an agent working from training data will pick
them. The Microsoft skills and the Azure releases pages both call this out
explicitly ([skills.sh listing](https://www.skills.sh/microsoft/skills/azure-storage-blob-rust),
[latest releases](https://azure.github.io/azure-sdk/releases/latest/rust.html)).
This belongs in AGENTS.md "Known traps" verbatim.

### 3.4 rustdoc-first workflow

`cargo doc --open` (with `--document-private-items` when working on internals)
gives the local, version-exact reference without network; doctests in `///`
examples are compiled and executed by `cargo test`, so the doc surface is
testable and drift-detectable ([rustdoc docs](https://doc.rust-lang.org/rustdoc/write-documentation/documentation-tests.html)).
Pair this with `rustup doc` for std/reference content offline.

### 3.5 Stale-training-data risk

The risk is concrete, not hypothetical: Anthropic caught Claude appending
"2025" to search queries purely from prior bias, and recommends fixing it via
tool/skill descriptions rather than prompting
([writing tools for agents, 2025-09-11](https://www.anthropic.com/engineering/writing-tools-for-agents)).
For the port this shows up as hallucinating removed APIs (axum 0.7 syntax,
sqlx 0.8 method names, the plural Azure crates). Defenses, in order of
effort: (1) the AGENTS.md freshness instruction (§4.1), (2) a skill
frontmatter that says "verify crate APIs via find-docs, never from memory",
(3) clippy/tests noticing drift behind us, (4) `Cargo.lock` + `--locked`
keeping surprise upgrades out.

## 4. Concrete integration plan for this repo (ranked)

> Applied 2026-11-07 on the agent-tooling PR: 4.1 (nested
> `backend-rs/AGENTS.md`), 4.2 (the four vendored skills,
> provenance in each frontmatter), 4.6 (root AGENTS.md) and the find-docs
> trigger extension (§3.1). Items 4.3–4.5 (rust-toolchain.toml, workspace
> `[lints]`/clippy, deny.toml) belong to the backend-rs workspace itself and
> land with wave 1 (#219); the nested AGENTS.md already encodes what they
> must look like.

4.1 **Write `src/archive/backend-rs/AGENTS.md` per the §1.4 outline.** Must
include the freshness block (verbatim-ish):

> **Documentation lookup is mandatory.** For any crate API question
> (tokio, axum, tower-http, sqlx, azure_*, pdfium-render, lettre,
> webauthn-rs, tracing, opentelemetry, reqwest, clap), run the find-docs
> skill (`npx ctx7@latest library <crate> "<specific query>"`) before
> writing code from memory. docs.rs is the API reference of record
> (`<https://docs.rs/<crate>>`). If you change a crate version in
> Cargo.toml, read that version's release notes/changelog before porting.
> Do not silently fall back to training data.

4.2 **Vendor 4–5 skills into `.agents/skills/`** (commit them, no registry
dependency at runtime — OpenCode picks up `.agents/skills` automatically,
[skills docs](https://opencode.ai/docs/skills/)):
  - `azure-identity-rust` + `azure-storage-blob-rust` from microsoft/skills
    (MIT; strip nothing — they are short and version-safe). Snapshot provenance
    (repo commit hash + date) in each frontmatter `metadata`.
  - `axum` from terminalskills/skills minus any dependency-version pins.
  - `rust` baseline (one, trimmed; ericrisco or the Apollo guidelines).
  - Leave find-docs as the only freshness mechanism; queue-SDK and AG-UI
    emitter skills do not exist yet — write our own `azure-storage-queue-rust`
    and `ag-ui-emitter` skills when those waves start, modeled on the
    Microsoft skill format (AGENTS.md + skill split per Factory's rule).

4.3 **Add `rust-toolchain.toml`** at `src/archive/backend-rs/` pinned to the
exact current stable patch (1.99.x as of 2026-11-07 — re-check
[channel-rust-stable.toml](https://static.rust-lang.org/dist/channel-rust-stable.toml)
at implementation), `components = ["rustfmt", "clippy"]`, `profile = "minimal"`.
Commit Cargo.lock, use `--locked` in commands, and schedule a bump (six-week
release cycle) rather than floating
([rustup overrides](https://rust-lang.github.io/rustup/overrides.html)).

4.4 **Workspace `[lints]` + `clippy.toml`** instead of scattered attributes;
start conservative (deny `unwrap_used` in i/o paths, warn pedantic-narrow) and
grow from real findings. `cargo fmt --all --check` runs first in any check
sequence; on the Pi, sequence fmt → clippy → nextest/tests **one at a time**
per root AGENTS.md memory rules.

4.5 **`deny.toml` + cargo-deny check** (advisories + license ids + duplicate
bans for the azure plural/singular crates — with cargo-deny you can literally
deny `azure_storage_blobs`/`azure_storage_queues` to make the drift trap fail
the build) ([cargo-deny](https://embarkstudios.github.io/cargo-deny/)).

4.6 **Root AGENTS.md touch-ups**: add `backend-rs` to Repository Layout; note
that nested AGENTS.md files are additive in OpenCode V2; keep the
single-heavy-job and `.NET` rules, and defer wiping .NET command examples to
when the C# backend actually freezes (ticket #218 milestones).

## Sources

Primary/official, all verified live 2026-11-07:

- https://agents.md/ — convention site, nested files, FAQ (no date shown; AAIF-stewarded footer)
- https://v2.opencode.ai/docs/instructions/ — OpenCode V2 AGENTS.md loading/nesting behavior
- https://opencode.ai/docs/rules/ — OpenCode rules docs (fallback examples)
- https://opencode.ai/docs/skills/ — OpenCode skill discovery & `.agents/skills`
- https://www.anthropic.com/engineering/claude-code-best-practices — concise/iterative instruction guidance
- https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills — skills anatomy & best practices (2025-10-16)
- https://www.anthropic.com/engineering/writing-tools-for-agents — model-bias/stale-training example (2025-09-11)
- https://www.humanlayer.dev/blog/writing-a-good-claude-md — CLAUDE.md/AGENTS.md writing guidance (2025-11-25)
- https://docs.factory.ai/cli/configuration/agents-md — concrete do/don't (2025)
- https://addyosmani.com/agents/15-agents-md — history, 150-line guidance, per-directory pattern
- https://www.codeline.co/thoughts/repo-review/2025/agents-md-open-format-for-guiding-coding-agents (2025-10-25) — spec thinness, CLAUDE.md relationship
- https://aicoding.club/docs/tutorials/agents-md-guide — 32 KiB budget, testing agent instructions
- https://s-celles.github.io/ai-config/docs/agents-md — AAIF timeline (verified 2025-12-28)
- https://github.com/anthropics/skills — official skills repo; no Rust skills
- https://agentskills.io — Agent Skills standard
- https://github.com/microsoft/skills (+ `.github/plugins/azure-sdk-rust/skills/*`) — first-party Azure Rust crate skills (MIT, Jan/Feb 2026)
- https://www.skills.sh/microsoft/skills/azure-storage-blob-rust — skill listing + install command + crate-naming warning
- https://github.com/terminalskills/skills and https://skills.sh/terminalskills/skills/axum — axum skill (v1.1.0, 2026-10-29)
- https://www.skillsdirectory.com/skills/ericrisco-rust — Rust baseline skill listing (2026-09-02)
- https://claude-plugins.dev/skills/@TerminalSkills/skills/axum — cross-agent install to `.agents/skills` (2026-03-22)
- https://github.com/samuelpkg/samuel-axum/blob/main/SKILL.md — example of stale version pins to avoid
- https://rust-lang.github.io/rustup/overrides.html — rust-toolchain.toml spec
- https://safeguard.sh/resources/blog/pin-the-rust-toolchain-with-rust-toolchain-toml (2026-10-19) — pinning practice, "stable floats" pitfall
- https://blog.rust-lang.org/2025/02/20/Rust-1.85.0/ — edition 2024 stable
- https://static.rust-lang.org/dist/channel-rust-stable.toml — current stable = 1.99.1 (2026-10-15)
- https://doc.rust-lang.org/clippy/configuration.html — clippy config
- https://rust-lang.github.io/rustfmt/ — rustfmt
- https://rust-analyzer.github.io/manual.html — rust-analyzer
- https://nexte.st — cargo-nextest
- https://embarkstudios.github.io/cargo-deny/ + https://github.com/rustsec/rustsec — supply-chain checks
- https://doc.rust-lang.org/rustdoc/write-documentation/documentation-tests.html — doctests
- https://llmstxt.org/ — llms.txt spec v2 (modified 2026-08-10)
- https://opentelemetry.io/llms.txt — the one YES in our inventory
- tokio.rs, docs.rs, axum.rs, lettre.rs, rustup docs — llms.txt absence probes (404/400, 2026-11-07)
- https://docs.rs/ — per-crate API reference (current: https://docs.rs/axum, https://docs.rs/sqlx, https://docs.rs/pdfium-render/0.9.5, https://docs.rs/webauthn-rs)
- crates.io API (`https://crates.io/api/v1/crates/<crate>`) — version table of §3.3 (2026-11-07 queries)
- https://devblogs.microsoft.com/azure-sdk/azure-sdk-release-may-2026 — Rust SDK GA incl. storage blob/queue 1.0
- https://azure.github.io/azure-sdk/releases/latest/rust.html and https://azure.github.io/azure-sdk/releases/2026-02/rust.html — current crate inventory, MSRV 1.88
- https://tokio.rs/blog/2025-01-01-announcing-axum-0-8-0 — axum 0.8 changes
- https://github.com/launchbadge/sqlx, https://github.com/open-telemetry/opentelemetry-rust, https://github.com/kanidm/webauthn-rs, https://github.com/ajrcarey/pdfium-render, https://lettre.rs — stack crate home pages
