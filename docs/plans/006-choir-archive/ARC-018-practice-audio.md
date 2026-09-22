---
id: ARC-018
status: done
phase: core
kind: slice
depends_on: ["ARC-016"]
touches: ["media-player", "catalogue-materials", "assets"]
external_inputs: []
---

# ARC-018 — Listen to a voice track through a full practice session

**Depends on:** [ARC-016](ARC-016-voice-file-batches.md).

## Outcome

A member selects the labelled voice track, listens/seeks in the browser, and
continues beyond the first 15-minute file-ticket lifetime.

## Acceptance criteria

- [x] Add accessible audio play/pause, seeking and volume with clear active-file
  and voice labels; preserve the authorized download option.
- [x] Renew file access through the API and preserve position/player state
  across renewed URLs and range requests.
- [x] Handle unsupported formats and temporary failures with an understandable
  state, without treating an original as a verified playable derivative.
- [x] Recheck membership/visibility for renewal; stop issuing tickets when access
  is removed, acknowledging that an existing ticket expires naturally.

## Slices

- [x] Backend A: renewal/revocation contract pinned (renewal re-checks active
  membership and member visibility; issued tickets keep their bounded lifetime).
- [x] Frontend B: ticket-expiry primitive (`restlaufzeitMs`) beside
  `fetchAssetAccess`.
- [x] Frontend C: accessible audio player component with play/pause, seek,
  volume, voice labels, single-active playback and scheduled renewal.
- [x] Frontend D: materials integration (Anhören button, player + download
  for audio; MIDI stays download-only for ARC-019).
- [x] Frontend E: browser tests for playback/seek, renewal with preserved
  position, unsupported format and temporary failure states.
- [x] Docs: README ARC-018 section, plan update, handoff to ARC-030/ARC-019.

## Verification

Play and seek past an accelerated ticket expiry, then verify actual long playback
in the hosted pilot. Deactivate a seeded membership in the test fixture and
assert renewal fails while an already-issued URL retains its bounded lifetime.

## Handoff and parallel work

Expose player/ticket-renewal primitives for concert video ARC-030. MIDI ARC-019
can proceed beside this slice; coordinate shared controls and material-list slots.

## Implementation and verification

Backend: the access endpoint already re-reads the whole authorization on every
call (`ArchiveAccessService` reads Identity state fresh, song visibility is
recomputed), so renewal is simply a fresh `GET /api/assets/{id}/access`. Two
new xUnit tests pin the revocation contract
(`AssetApiTests.MembershipRevocationStopsRenewalWhileIssuedTicketsStayBounded`,
`VisibilityRevocationStopsRenewalWhileIssuedTicketsStayBounded`): after a
seeded member is deactivated (or the song is unpublished), renewal answers 401
resp. 404 and no further tickets are issued, while the already-issued
view/download tickets keep their recorded bounded 15-minute lifetime — an
existing ticket expires naturally and is never extended server-side. Blob
read SAS naturally honour `Range` requests, which is what the browser audio
element uses for seeking.

Frontend: `lib/assets.ts` gains `restlaufzeitMs(expiresAt)` (unparsable
expiries count as expired). The new `components/audio-spieler.tsx` renders an
unstyled `<audio>` element plus accessible custom controls: play/pause toggle
(`Sopran abspielen`/`Sopran pausieren`), seek slider (labelled `Position
(Sopran)`, disabled until metadata), volume slider (`Lautstärke (Sopran)`),
tabular time display `m:ss / m:ss`, a visually hidden polite status
(`Sopran wird abgespielt`), and the section label `Audio-Spieler · Sopran`.
The materials area keeps at most one playing entry (`spielendesAudio` state);
inactive players pause themselves. Ticket renewal schedules a silent
`fetchAssetAccess` call 60 seconds before `expiresAt` (30-second retry on
failure), and on renewal the same `<audio>` element swaps `src` while the
captured position and play state are restored in `loadedmetadata` — playback
continues at the same position on the fresh URL. Any media error first
attempts one silent renewal (expired tickets and network failures surface
alike); if the failure persists, the state is understandable: unsupported or
undecodable content shows "Dieses Audioformat kann im Browser nicht
wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden."
(never marked as a verified playable derivative), transient errors show
"Audio konnte nicht geladen werden. Bitte erneut versuchen." with an
"Erneut versuchen" button, and 404/401 renewals map to their specific copy.
The audio `src` is only set after hydration so the browser cannot fire load
errors before React attaches handlers. In `noten-bereich.tsx` the audio group
offers "Anhören" (loads access, then player + authorized "Herunterladen"
link); MIDI stays download-only for ARC-019. The renewal primitive
(`fetchAssetAccess` + `restlaufzeitMs`) is the documented handoff for the
ARC-030 concert video player.

Verification: 198 backend xUnit tests green (2 new). The Playwright suite
passes 80 mocked-API checks (of 92; the 6 `shell.spec.ts` checks per project
need a live backend behind the proxy and run in CI against the real image),
including 8 new audio checks — playback/seek/volume/download preservation,
renewal across a simulated 3-second ticket expiry with continued playback at
the preserved position, unsupported-format state, and transient-failure
recovery via "Erneut versuchen". `pnpm run check` and `pnpm run build`
(static export) stay green. Real long playback in the hosted pilot remains
with ARC-044; the accelerated-expiry play-through used mocked blob bytes.
