# Incoming HTTP W3C correlation

`frontend::ProbeSkipper` skips exact `/alive` and `/health`, otherwise starts a
root request span and sets its OTEL parent from the pinned SDK's
`TraceContextPropagator::extract_with_context(Context::new(), carrier)`.
Only `traceparent` and `tracestate` enter that carrier. No baggage extraction,
header logging or user-defined attributes are introduced. Invalid/absent input
must not inherit an ambient task context. Owned consumer spawning still uses
`Origin::spawn_reading`; this fix does not replace that separate transfer.

Verified API references: tracing-opentelemetry0.34.0 `OpenTelemetrySpanExt::set_parent`
(returns Result), opentelemetry_sdk0.33.1 `TraceContextPropagator`, and exact pinned
opentelemetry0.33.1 propagation source. Context7 quota was exhausted; versioned
docs.rs and Cargo registry sources were used, not guessed APIs or an upgrade.

```sh
cargo build --workspace --examples --locked
dotnet build ../backend
node tests/browser/http-w3c-native.cjs
node tests/browser/lied-reading-native.cjs
```

The generic native gate uses actual frozen ASP.NET and product Rust Development
origins. For non-Development Rust sampling only, the tiny offline example uses
the same product Router and telemetry defaults with no auth/mail/storage/model
construction. That is not a production-auth startup test. The frozen host uses
Testing with ephemeral test keys; no frozen source is modified.

Actual decoded OTLP checks compare incoming trace ID, remote parent span ID,
new HTTP span ID and tracestate, not span names/substring presence. Missing,
malformed and zero-ID parents are independent fresh roots; invalid tracestate
does not discard a valid parent. Development AlwaysOn records incoming flag00
as sampled; the non-Development default ParentBased sampler omits it. Exact
health probes remain absent. Synthetic baggage is a privacy marker, not trusted
context. All children/collector sessions drain before terminal privacy replay.

The Lied native journey additionally checks remote → actual HTTP → owned model
span IDs/traces/tracestate for stateless cancellation and owned persisted work,
including disconnect, discard, shutdown and failed settlement. Model/ledger,
draft registry, lifetime and provider contracts are not changed by this fix.
Programme #231 must consume the shared fix and independently run its own child
chain controls; generic and Lied passes do not waive that requirement.
