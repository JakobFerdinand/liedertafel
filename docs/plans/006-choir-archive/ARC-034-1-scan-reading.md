---
id: ARC-034-1
status: planned
phase: core
kind: slice
depends_on: ["ARC-034", "ARC-021-1", "ARC-013-1"]
touches: ["extraction", "catalogue", "azure-jobs", "ai-evaluation"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-034-1 — Read scanned scores and prefill catalogue fields

**Depends on:** [ARC-034](ARC-034-pdf-extraction.md),
[ARC-021-1](ARC-021-1-luna-switch-and-budget-cap.md),
[ARC-013-1](ARC-013-1-field-provenance-and-proposals.md).

## Outcome

An editor uploads a scanned score that has no embedded text and finds title,
creators, voice, key and lyrics already filled in, marked "KI" and revertible.

## Acceptance criteria

- [ ] Add a second finite job, "AI reading", with its own identity (the only
  job identity with the OpenAI role), queue, size and timeout. The extraction
  job stays model-free and hands work over; `NoText` is no longer terminal.
- [ ] Step one, for `NoText` revisions: render pages in the job with a
  PDFium-based package that ships its native library, and have a named agent
  transcribe them. Up to 20 pages per document in requests of up to 10 images
  at a bounded resolution; longer documents are marked "teilweise gelesen" and
  an editor can request the rest. Store the text as the revision's text.
- [ ] Step two, for any revision with text: `ScoreTextAnalyzer` runs first and
  its facts are written with source `regex` into empty, unlocked fields. A
  second named agent derives only the fields still empty.
- [ ] Each derived field carries `sicher` or `unsicher` plus a verbatim quote.
  Code verifies the quote appears in the text. Only `sicher` with a verified
  quote is applied and badged; everything else becomes a proposal. A new song
  identity is never created automatically.
- [ ] Writes go through the ARC-013-1 shared write service. The job has no
  other write access. Text in the scan is data, never instructions.
- [ ] The job reads drafts (unpublished uploads); its output stays invisible to
  members until the record is published.
- [ ] At the budget cap the job row becomes `WaitingForBudget`, the queue
  message is acknowledged, and the dispatch sweep re-enqueues it when the month
  changes or the cap is raised. The editor sees that status; manual entry is
  never blocked.
- [ ] A fixed regression set of real scans with expected fields, run on demand
  with pass rate and cost per document recorded. CI uses a scripted stand-in
  and never calls the provider.

## Verification

Upload a text PDF, a clean scan and a poor handwritten scan. Confirm the text
PDF makes no model call for fields the regex found, the scan is prefilled with
badges, uncertain fields land in "Vorschläge", and a hand-edited field survives
a retry. Run the evaluation set and record results.

## Handoff and parallel work

ARC-035 searches the text this slice stores. ARC-037 imports feed scans into
this job under the budget cap. This reverses the PRD's earlier OCR exclusion;
optical music recognition stays out. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
