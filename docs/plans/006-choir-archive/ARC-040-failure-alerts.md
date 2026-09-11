---
id: ARC-040
status: planned
phase: core
kind: slice
depends_on: ["ARC-032", "ARC-035"]
touches: ["observability", "service-defaults", "azure-alerts", "release-workflow", "azure-jobs"]
external_inputs: ["azure-maintainer-access", "maintainer-alert-destinations"]
---

# ARC-040 — A maintainer receives an actionable failure alert

**Depends on:** [ARC-032](ARC-032-pdf-extraction.md),
[ARC-035](ARC-035-copy-reviewed-folder.md).

## Outcome

A failed extraction/import job or release produces an actionable notification
that the primary or second maintainer can use to find the relevant failure.

## Acceptance criteria

- [ ] Correlate bounded structured diagnostics with release, work and asset IDs;
  connect failed job/release signals to configured maintainer destinations.
- [ ] Preserve the existing development OTLP path to Aspire for logs, traces and
  metrics; choose production exporters separately and prevent routine local runs
  from sending data to production monitoring.
- [ ] Provide a concise operator path from alert to existing job status/retry or
  maintenance repair, with ownership and relevant documentation links.
- [ ] Select supported bounded log retention and volume settings; redact codes,
  secrets, session cookies, signed URLs and unnecessary document/source contents.
- [ ] Distinguish terminal failures from controlled transient retries and avoid
  notification storms. Do not add a keep-alive website monitoring loop.

## Verification

Inject one safe job failure and a simulated release failure, verify receipt by
the intended destinations, and trace to the correct run. Inspect representative
logs for secret leakage and verify idle monitoring does not wake Neon/the web app.
Repeat a local job failure through AppHost and verify its correlated trace/logs
and metrics reach Aspire after finite-job shutdown.

## Handoff and parallel work

Share alert routing/diagnostic conventions with ARC-041 while coordinating Bicep
alert resources. The monthly review/runbook should be executable by the second
maintainer rather than rely on the original developer's memory.
