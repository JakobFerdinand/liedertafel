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

- [ ] The regex `ScoreTextAnalyzer` still runs first on embedded text. The
  model is called only for revisions that end as `NoText` and for fields the
  regex left empty.
- [ ] For `NoText` revisions, render pages to images and read them with Luna in
  the existing extraction job; respect the limit of 10 images per request and
  bound pages per document. Store the read text as the revision's text so
  search and chat can use it.
- [ ] Return structured fields with a confidence per field and write them
  through the ARC-013-1 path: low-stakes fields above the threshold are
  applied and badged, the rest become proposals. A new song identity is never
  created automatically.
- [ ] The job has no write access beyond its own extraction result and the
  ARC-013-1 automated write path. Text in the scan is data, never instructions.
- [ ] The job reads drafts (unpublished uploads); its output stays invisible to
  members until the record is published.
- [ ] Calls go through the capped path; at the cap the work stays queued and
  the editor sees a status that says so. Manual entry is never blocked.
- [ ] Extend the vision evaluation from ARC-021-1 into a fixed regression set
  with expected fields and record its pass rate and cost per document.

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
