---
id: ARC-019
status: done
phase: core
kind: slice
depends_on: ["ARC-016"]
touches: ["midi-player", "catalogue-materials"]
external_inputs: []
---

# ARC-019 — Listen to MIDI and change practice tempo

**Depends on:** [ARC-016](ARC-016-voice-file-batches.md).

## Outcome

A member plays an authorized MIDI file inside the app and changes tempo without
installing another application.

## Acceptance criteria

- [x] Select and justify a bounded browser synthesis/parsing approach with
  compatible licensing and modest downloadable resources.
- [x] Provide play/pause, seeking, volume and tempo adjustment with basic sound;
  initialize audio through a user gesture on supported mobile browsers.
- [x] Fetch bytes only through existing authorized file access, retain MIDI
  download, and label the musical version/voice clearly.
- [x] Stop/release audio resources on navigation and report unsupported/corrupt
  files without breaking other catalogue material. No track mixer or audio export.

## Slices

- [x] Frontend A: dependency-free SMF parser (`lib/midi.ts`) with tempo map,
  running status and corrupt-file guards.
- [x] Frontend B: `MidiSpieler` component — Web Audio oscillator synthesis,
  play/pause/seek/volume/tempo, user-gesture context creation, unmount release.
- [x] Frontend C: materials integration (Anhören button, player + retained
  download for MIDI; single-active rule across audio and MIDI players).
- [x] Frontend D: browser tests for playback/seek, live tempo change, corrupt
  file and transient failure recovery.
- [x] Docs: README ARC-019 section, plan update.

## Verification

Use a representative multi-track MIDI fixture to verify tempo changes, seek and
pause/resume on desktop and phone browsers; test malformed input and permission
denial. Confirm the app does not silently depend on a remote conversion service.

## Handoff and parallel work

This player can run alongside audio work ARC-018 using the shared material/ticket
contract. Coordinate the catalogue launch button slot, not their playback engines.

## Implementation and verification

Approach: a dependency-free SMF parser plus Web Audio oscillator synthesis,
both written in-repo — zero downloadable resources, repo license, fully offline,
no remote conversion service (the AC's test). Soundfont/sampler libraries
(smplr, midi-js-soundfonts) were rejected because their samples load from
third-party CDNs at play time, a runtime remote dependency the archive cannot
own, and the AC only requires "basic sound".

Frontend: `lib/midi.ts` parses Standard MIDI Files (formats 0/1, running
status, piecewise tempo map to a seconds timeline, per-(channel,pitch) note
stacks; SMPTE timebase, format 2, desynchronised event streams and >4 h
pieces throw `MidiFehler`; zero-note files are legal).
`components/midi-spieler.tsx` fetches
the bytes once through the ticketed `viewUrl` (CORS note in the README), parses
after mount (static-export safe), and synthesizes with one triangle oscillator
plus envelope per note into a master gain (velocity-scaled, with a dynamics
compressor keeping dense chords below full scale).
Player error copy and time formatting are shared with the audio player
(`SpielerFehler`, `ladeFehlerAusUrsache`, `zeitText` in `lib/assets.ts`).
The position lives on the file's own tempo timeline; an anchor pair
(wall time, base time) plus a 50–150 % tempo factor maps base to wall time, so
seek and tempo changes re-anchor and reschedule — playback continues at the
new rate from the current position, with already-sounding notes cut at the
switch (acceptable for basic sound). The AudioContext is created and resumed
synchronously inside the play-button click (mobile user-gesture requirement).
Reaching the end stops and re-arms from 0; `aktiv=false` pauses (single-active
rule shared with the audio player via `spielendesAudio`); unmount stops all
sources and closes the context. In `noten-bereich.tsx` the MIDI group now
offers "Anhören" (loads access, then player + retained authorized
"Herunterladen" link), labelled `MIDI-Spieler · <Stimme>`. Errors mirror the
audio player: 404/401 with specific copy, transient failures with "Erneut
versuchen" (re-fetches fresh access first), and corrupt/unsupported files
report the non-playable format with no retry, download retained. Because the
bytes are fully in memory, no mid-playback ticket renewal is needed.

Verification: `pnpm run check` and `pnpm run build` (static export) stay
green. The parser was additionally exercised standalone against a generated
format-1 fixture (running status, mid-file set-tempo, SMPTE rejection,
desync rejection) during development. The Playwright suite passes 92
mocked-API checks (both projects), including 12 new MIDI checks: playback with
controls and retained download, seek to a position, pause frozen at its
position and resumption from there, live tempo change to 150 % continuing
without a position jump and measurably faster progress, corrupt-file state
leaving the audio entry untouched, transient-failure recovery via "Erneut
versuchen" with re-fetched access, and a denied 401 byte fetch showing the
expired-session copy on load and on the retry path. The 6
`shell.spec.ts` checks per project still require a live backend and run in CI.
Real long listening on devices remains with the hosted pilot (ARC-044).
