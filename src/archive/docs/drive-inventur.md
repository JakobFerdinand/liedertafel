# Quellen-Inventur (#98)

Status: discovery instrumentation committed; the real measurement stays with
the authorized maintainers. Ticket [#98](https://github.com/JakobFerdinand/liedertafel/issues/98)
part of [#96](https://github.com/JakobFerdinand/liedertafel/issues/96). All
committed artifacts are sanitized; architecture §14 keeps the inventory
deterministic — no model call belongs here.

## What the inventory observes (2026-09-14 inspection, ticket #98)

Marking convention: **[beobachtet]** = read from the ticket's inspection
notes; **[Vermutung]** = filename-based inference that still needs editor
confirmation. The notes describe folder listings only — file contents, audio
codecs and ratio measurements were never inspected, and the initial page list
was truncation-prone (not a complete inventory).

### Sample A — flat score folder [beobachtet]

One full-score PDF plus separate PDFs for Bass 1, Bass 2, Tenor 1, Tenor 2 and
Solo; an Excel catalog (`.xls`) sits in the same folder. At the archive root
both the folder itself and a shortcut onto it are listed [beobachtet]. The
mapping keeps each PDF-named Noten role a filename guess and treats the
shortcut as a duplicate-listing hazard: resolve its target and import the
folder once [Vermutung]. The spreadsheet is a separate-inspection candidate,
never an automatic score [Vermutung].

### Sample B — file-type subfolders [beobachtet]

`1. Noten` holds `PT-… .pdf` (~297 KB shown); `2. Media` and
`3. Notierprogrammdaten` were empty in the listing. **`PT` means Partitur,
maintainer-confirmed** [beobachtet]. Mapping: the subfolders classify assets,
not arrangements; empty folders create no records. Whether this PDF is a scan
is unconfirmed — the fixture uses a scan PDF as the projected probe [Vermutung].

### Sample C — duplicate names, voice files, notation source [beobachtet]

Eleven listed files: five PDFs (full score, T1, B2 and two identically named
B1 files), five MP3s (~1.6–1.8 MB displayed) and one `.capx` whose name ends
in `3`. The two B1 PDFs differ in displayed size/date (~21 KB, 2023-10-24 vs
~316 KB, 2023-11-01): **suspected accidental duplicate, relationship
unresolved**; neither byte equality nor a correction relationship was
established [beobachtet]. T2 audio exists with no T2 PDF. Mapping keeps both
B1 candidates, never deduplicates by filename and never prefers the newer
date; a trailing `3` alone does not establish version identity, and a `.capx`
is never MIDI evidence [beobachtet].

### What stays unobserved [beobachtet]

Multiple arrangements, transpositions, corrected scores and a MIDI example
were not found in the inspected listings; the corpus' `Midi-Beispiel` is a
projected case. Historical programme selection (root-level event filenames
were visible, contents unevaluated) is editorial work with approximate dates
per ADR-0001 (one editable Programm per Auftritt).

## Committed artifacts

- `tests/archive/fixtures/drive-inventur/` — the sanitized corpus with
  `mapping.json` (folder-to-catalogue expectations for #143) and its README.
- `tests/archive/backend/InventurTests.cs` — keeps corpus, mapping and report
  truthful; the mapping's flags vocabulary is walk-fact only.
- `src/archive/backend/Inventur/` — the deterministic walker and the
  `--inventur` console command.

## Maintainer runbook against the real source

1. **Obtain the link**: get the current read-only Drive root link from Jakob.
   It lives in the git-ignored working note
   `src/archive/.local/source-inventory.md` (create it if missing) next to the
   exact sample-to-source references; never commit that file, real links,
   downloads or IDs. The `.local/` ignore rule and its "not distributed by
   cloning" property are already in place (#98's §Access handoff).
2. **Produce a local export** of the representative folders (Drive download
   or rclone), preserving the folder tree. Record which export mode you used
   in the `.local` note — including how same-name duplicates and shortcuts
   appear on disk (`… (1).pdf`, `.url`, `.webloc`) — this convention is still
   unverified [Vermutung].
3. **Run the inventory** from the repository root:

   ```bash
   dotnet run --project src/archive/backend -- --inventur "/pfad/zur/export-ansicht" \
     --model gpt-6-luna --out src/archive/.local/inventur.json
   ```

   The command never touches the network, database or a model; `--model` only
   names the `Archive:Ai:Models` price pair for the estimate (without it the
   report keeps the `estimatedEur` empty and says so).

4. **Record per the ticket's open list**: total file count, aggregate bytes,
   largest files (upload resumability → ARC-017), MIDI examples (`midi`
   flags), scanned vs extractable-text PDFs with page counts (`pdf.text =
   "scan"`/`"embeddedText"`), unreadable documents and shortcut targets
   (`shortcuts`). The report's `duplicates` list is a candidate set only —
   resolve it with an editor, never by filename.
5. **Codecs and ratios**: the walk sniffs containers from leading bytes
   (MP4/M4A/WebM/MP3/WAV/FLAC/Ogg via `RecordingFormats`, MIDI, PDF); actual
   codecs need `ffprobe` per file, and playback/original ratios can only be
   measured once samples are uploaded through the archive and their playback
   copies exist (1080p H.264/AAC per ADR-0001). Record measured ratios in the
   `.local` note; do not treat the assumed ~20 % derivative ratio as fact.
6. **Cost reconciliation**: multiply the report's scanned-page totals with
   the `assumptions` in `readingCost` (see formula below) and check the
   result against the EUR 15 monthly cap (`Archive:Ai:MonthlyCapEur`) and the
   EUR 10 target from the plan; a price change under `Archive:Ai:Models`
   recomputes on the next run.
7. **Historical programmes**: skim the root-level event-document names with
   an editor, pick representative concerts with approximate dates and mark
   unconfirmed evidence explicitly (ADR-0001: no confirmed/mention states —
   one editable Programm decides).

## ARC-034-1 cost formula (analytical, no model call)

The estimate mirrors the reading job's own bounds:

```text
read(PDF)    = min(pages, Archive:Reading:MaxPagesPerRequest = 20)
calls(PDF)   = ceil(read / Archive:Reading:ImagesPerCall = 10)
input/call   = ImagesPerCall × Archive:Reading:EstimatedTokensPerImage (2500)
             + 1000 prompt tokens   (as ScanReadingModel.WorstCaseTranscription reserves it)
output/call  = Archive:Reading:TranscriptionMaxOutputTokens (5000)
EUR          = Σ calls × ( input/1e6 × priceInputEUR/M-token
                          + output/1e6 × priceOutputEUR/M-token )
```

Prices come from `Archive:Ai:Models:<model>`; a model without a price pair
answers `estimatedEur: null` with a note. Text-bearing PDFs cost nothing
(the extraction job is deterministic); only `pdf.text = "scan"` documents
enter the formula.

## Handoff

- #143 bulk import consumes `tests/fixtures/drive-inventur/mapping.json` (the
  `MappingFileStaysTruthfulToTheFixtureCorpus` test defines the contract);
  the importer keeps the mapping's rules: empty folders import nothing,
  shortcuts deduplicate, both unresolved duplicates survive, incomplete voice
  sets are supported, and `.xls`/`.capx` need separate inspection
  (implemented on the "Ordner importieren" card, see README "Bulk folder
  import (#143)").
- ARC-017 (upload resumability) receives `largest` once the real run exists.
- ARC-034-1 planning receives `readingCost` (scanned pages × page counts).
