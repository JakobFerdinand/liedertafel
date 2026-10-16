---
status: accepted
---

# Flat Lied model and upload-first editing in the choir archive

Decided with the maintainer on 2026-10-16, after implementing most of the
original plan in #96 showed it was too complex for a small editor team and made
everyday work slow. Terms are defined in `GLOSSARY.md`.

## Decision

- **One flat Lied.** The catalogue is Lied → Dateien. The former
  Song → Arrangement → Musical version hierarchy is removed. A different
  arrangement or key of the same music is a separate Lied with its own title,
  with no link between the two.
- **No file revisions.** Replacing a Datei replaces it; the old file goes to the
  seven-day trash. Dateien are Noten (Partitur or one Stimme), Übe-Dateien
  (audio or MIDI, per Stimme), Aufnahmen and Sonstiges. An Aufnahme belongs to a
  Lied (optionally naming the Auftritt it came from) or to an Auftritt.
- **Upload-first.** Creating a Lied or an Auftritt starts by dropping files. The
  AI reads file names and content and fills the form; the editor checks and
  saves once. If the read title matches an existing Lied, the form offers to
  add the files there instead. Entry without a file is a secondary link.
- **Suggestions only inline.** AI values appear prefilled and editable in the
  form being worked on and become ordinary data on save. There is no separate
  review queue, no stored per-field provenance or AI badge for catalogue
  fields, and no published/unpublished state; an upload whose reading is still
  running waits in an "Angefangen" list, the only unfinished state.
- **Simpler Auftritte.** One editable Programm per Auftritt, which is also the
  record of what was sung: no programme revisions, no separate confirmation
  step, no `confirmed`/`mention` evidence states. Approximate historical dates
  stay.
- **Audio analysis is in scope.** Uploaded videos get a playable copy
  (original resolution up to 1080p, H.264 at high quality, AAC 192 kbit/s) with
  the original kept. A concert Aufnahme is transcribed with timestamps, and
  the AI suggests a Zeitmarke per Lied, or the whole Programm when none exists.
- **Cut:** the editor assistant, semantic search with embeddings, lyric
  translation, duplicate merge tools, member correction requests, and the
  pilot/launch gates. Plain search over title, Liedtext and text read from
  Noten is enough for about 500 Lieder.
- **Data reset.** Accounts and passkeys are kept; all catalogue, Auftritt and
  file data (test data only) is dropped by a migration.

## Considered options

- *Keep the hierarchy but hide it in the UI*: rejected. Every screen, query and
  AI prompt would still carry three levels nobody uses.
- *Auto-apply certain AI values with provenance and a review queue* (the
  previous design): rejected. It spread one decision over two screens and
  needed provenance locking to stay correct. Checking prefilled values in the
  form the editor already has open is faster and needs no extra state.
- *Pause/applause detection for Zeitmarken*: rejected as the primary method.
  It is unreliable when Lieder follow each other without clear applause;
  transcription also recognises spoken announcements.

## Consequences

- `src/archive/docs/architecture.md` sections on file revisions, provenance,
  proposals and embeddings are superseded where they conflict with this ADR.
- Issue titles no longer carry `ARC-xxx` numbers; order comes from milestones
  (Vereinfachung, Bestand, Betrieb).
