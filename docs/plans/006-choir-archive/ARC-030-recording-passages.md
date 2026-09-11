---
id: ARC-030
status: planned
phase: core
kind: slice
depends_on: ["ARC-021", "ARC-028", "ARC-029"]
touches: ["recordings", "song-history", "search", "db-migrations"]
external_inputs: []
---

# ARC-030 — Jump from a song to its passage in a concert recording

**Depends on:** [ARC-021](ARC-021-repertoire-filters.md),
[ARC-028](ARC-028-concert-recording.md),
[ARC-029](ARC-029-song-history.md).

## Outcome

An editor captures a song's start/end while watching, and members find and play
that passage from the song's history or recorded-material search results.

## Acceptance criteria

- [ ] Select an existing performance occurrence and capture/edit start/end
  timestamps using the player; validate ordering, duration and event ownership.
- [ ] Link the segment to recording and performance IDs without copying the
  performance; a second recording can document that same occurrence.
- [ ] Add authorized passage links to history, opening the player at the correct
  position with a clear segment end and working renewed file access.
- [ ] Complete the recording-availability filter from ARC-021 through these
  explicit song/performance relationships, respecting visibility/deletion.
- [ ] Whole-recording playback continues to work when only some songs are marked.

## Verification

Index two songs in one video and one of those performances in a second recording.
Verify precise jumps, invalid/cross-event bounds, recorded-material filtering,
partial indexing and unchanged performance totals after adding the second source.

## Handoff and parallel work

Own the integration between player, search and history rather than leaving it
for the launch gate. Coordinate their existing extension slots; import review
and score revision work can proceed separately.
