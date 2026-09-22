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

Supply the mapping fixtures to ARC-036 and largest-file characteristics to
ARC-017. This discovery can run alongside application and infrastructure work;
missing Drive access is an external blocker, not a code dependency.

## Initial source inspection — 2026-09-14

The maintainer supplied a read-only Drive folder link. Anonymous web requests
successfully displayed the root listing and the three selected song folders,
including nested folders. A service account or rclone setup was not required
for this initial inspection. File downloads and complete recursive enumeration
have not yet been verified.

The ordinary Drive folder page initially exposed only the first 50 entries.
Drive's embedded folder view exposed additional entries, allowing the two
maintainer-selected examples to be located. Do not treat the initial page as a
complete inventory or use its entry count as the archive total.

### Access handoff and restricted source references

Authorized maintainers obtain the current source link from Jakob. The local,
git-ignored working note `src/archive/.local/source-inventory.md` records the
root link and exact sample-to-source references for this inspection. It is not
distributed by cloning the repository. Keep real source IDs, links, downloads,
and future comparison evidence in restricted working material; the cases below
use neutral sample labels. These notes are not yet the reusable mapping fixtures.

### Observed conventions and proposed mapping

**Sample A — flat score folder**

- One full-score PDF and separate PDFs for Bass 1, Bass 2, Tenor 1, Tenor 2,
  and Solo; also an Excel catalogue (`.xls`) in the same folder.
- Both a folder and a shortcut to that same folder appear at the archive root.
- Proposed mapping: one song candidate with associated score/voice files. The
  spreadsheet needs separate inspection rather than automatic treatment as a
  score. Resolve shortcut target identity to avoid importing the same folder twice.

**Sample B — file-type subfolders**

```text
Sample B/
├── 1. Noten/
│   └── PT-Sample B.pdf
├── 2. Media/               (empty in the inspected listing)
└── 3. Notierprogrammdaten/ (empty in the inspected listing)
```

- The score's displayed size is approximately 297 KB.
- **Maintainer-confirmed: `PT` means Partitur (full score).**
- Proposed mapping: the three subfolders classify assets, not arrangements.
  Empty folders create neither asset records nor inferred media/source files.

**Sample C — scores, practice audio, notation source, and duplicate names**

- Eleven listed files: five PDFs, five MP3s, and one `.capx` file.
- PDFs: full score, T1, B2, and two files with the identical B1 filename.
- MP3s: full-score-labelled audio plus T1, T2, B1, and B2, with displayed sizes
  around 1.6–1.8 MB. Notation-source candidate: a `.capx` file whose name ends in `3`.
- The two B1 PDFs have different displayed sizes/dates: approximately 21 KB
  (2023-10-24) and 316 KB (2023-11-01).
- The maintainer does not know their relationship and suspects human error.
  Record **suspected accidental duplicate; relationship unresolved**. Neither
  byte equality nor a correction/replacement relationship has been established.
- T2 audio exists, but no T2 PDF is shown in this folder.
- Proposed mapping: associate files by song, asset role, and voice, subject to
  arrangement/version review. Support incomplete voice-file sets. Keep both B1
  candidates for comparison; do not deduplicate by filename or prefer the newer
  date automatically. A trailing `3` alone does not establish version identity.

These are folder-listing observations and filename-based proposals. PDF text,
musical content, audio codecs, and notation-source contents were not inspected.
Displayed sizes are rounded metadata, not measured storage/derivative ratios.

### Remaining work before completion

- Verify downloads and complete recursive inventory, including source identity
  and shortcut handling; measure file counts, aggregate size and largest files.
- Inspect contents and find/confirm examples of multiple arrangements,
  transpositions, actual corrected scores, and MIDI. A `.capx` file is not itself
  evidence of a MIDI example.
- Compare scanned versus extractable PDF text, inspect audio codecs, and measure
  playback/original ratios. Reconcile measured inputs with the EUR 10 target.
- Locate and select historical concert programmes with an editor, including
  approximate dates and uncertain evidence. Root-level event-document filenames
  were visible, but their contents and suitability have not been assessed.
- Create synthetic/sanitized fixtures and mapping expectations for ARC-036, with
  restricted traceability back to these real conventions and editor review.
- Supply measured largest-file characteristics to ARC-017.

The source link is sufficient to continue investigating; another sample export
is not currently required. Ask the editor targeted questions as ambiguities are
found. Source access and these initial observations do not complete ARC-002's
acceptance criteria.
