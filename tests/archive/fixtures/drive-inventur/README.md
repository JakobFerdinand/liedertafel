# Drive-Inventur-Fixtures (#98)

Sanitized fixture corpus for the source inventory (#98) and the bulk import
(#143). Every file here is synthetic; names, sizes and links carry no real
source identifiers. The predecessor of each fixture is named in
`mapping.json` (`quelle`, `bemerkung`) and in `src/archive/docs/drive-inventur.md`.

## What the corpus embodies

- **Sample A** — flat score folder: one full score PDF, five per-voice PDFs
  (Bass 1/2, Tenor 1/2, Solo) and an `.xls` catalog file in the same folder,
  plus a root-level `.url` shortcut onto the same folder.
- **Sample B** — file-type subfolders (`1. Noten` with `PT - … .pdf`,
  empty `2. Media` and `3. Notierprogrammdaten`); `PT` means Partitur
  (maintainer-confirmed). Its PDF is the corpus' scan probe.
- **Sample C** — score, voice PDFs, voice-labelled MP3s, the `.capx`
  notation-source candidate and two B1 PDFs with the same displayed name
  exported as `… B1 (1).pdf` (different sizes, unresolved duplicate).
- **Midi-Beispiel** — a projected (not yet observed) MIDI Übe-Datei case.

PDFs are built with the same helpers the reading tests use (`ScanFixtures` of
tests/archive/backend): text pages carry extractable text, scan pages carry
only JPEG picture data and no text objects. MP3s are ID3 header plus one real
128 kbit/s Layer-III frame header; the `.xls` is an OLE compound-file header;
the `.mid` is a `MThd`/`MTrk` skeleton; the `.capx` is a tiny XML document.

## Contract

- `mapping.json` declares, per fixture file, how #143 maps it to a Lied/Datei
  (`art` = noten/uebe/sonstiges, `partitur`, `stimme`) or why it stays without
  a Lied (`ohneLiedzuordnung`: `shortcut` / `separateInspect`). Fixture facts
  observed by the walk are mirrored in `flags` — nothing else.
- The tests in `tests/archive/backend/InventurTests.cs` keep corpus, mapping
  and the `--inventur` report in agreement; extend all three together.

## Maintainer steps against the real source

`src/archive/docs/drive-inventur.md` records how to obtain the restricted
source link, run `--inventur` against a real export and record what remains
open (codecs, playback ratios, historical programmes).
