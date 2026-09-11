---
id: ARC-002
status: planned
phase: core
kind: discovery
depends_on: []
touches: ["import-fixtures", "planning"]
external_inputs: ["drive-sample-access"]
---

# ARC-002 — Measure representative catalogue folders and media

**Depends on:** None.

## Outcome

An editor and maintainer can identify which real source conventions the first
import supports and demonstrate them with a small, reusable fixture set.

## Acceptance criteria

- [ ] Inspect representative Drive folders: one simple song, multiple arrangements,
  transpositions, corrected scores, voice files, duplicate names, and ambiguity.
- [ ] Measure aggregate source size, maximum file size, codecs, MIDI examples,
  extractable versus scanned PDF text, and actual playback/original size ratios.
- [ ] Select representative historical programmes and concerts for the pilot,
  including approximate dates and unconfirmed programme evidence.
- [ ] Write a proposed folder-to-catalogue mapping and inventory summary with
  explicit unknowns. Preserve source identifiers in restricted working material.
- [ ] Commit only synthetic/sanitized mapping fixtures and expectations; record
  how authorized maintainers obtain representative real samples separately.

## Verification

Have an editor trace every fixture back to a real convention and explain its
expected song/arrangement/version/file association. Reconcile the size estimate
with the EUR 10 target without treating the assumed 20% derivative ratio as fact.

## Handoff and parallel work

Supply the mapping fixtures to ARC-034 and largest-file characteristics to
ARC-017. This discovery can run alongside application and infrastructure work;
missing Drive access is an external blocker, not a code dependency.
