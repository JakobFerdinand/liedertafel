---
id: ARC-039
status: planned
phase: core
kind: slice
depends_on: ["ARC-017", "ARC-028", "ARC-049"]
touches: ["storage", "azure-media", "recordings"]
external_inputs: ["azure-maintainer-access"]
---

# ARC-039 — Keep a playable copy online while storing its original in Cold

**Depends on:** [ARC-017](ARC-017-large-upload-resume.md),
[ARC-028](ARC-028-concert-recording.md),
[ARC-049](ARC-049-hosted-private-file.md).

## Outcome

An editor verifies a recording's online playback copy and moves its separate
preservation original to Cold storage without interrupting member playback.

## Acceptance criteria

- [ ] Extend the working live storage configuration from ARC-049 with the selected
  Hot/Cold policy and any bounded tier-transition worker permissions.
- [ ] Keep member-facing files Hot; permit Cold originals only with verified
  usable online material. If one object serves both roles, keep that object online
  without manufacturing another copy just for tier separation.
- [ ] Persist original/playback/tier state, handle failed tier transitions, and
  expose an understandable editor state rather than silently claiming success.
- [ ] Preserve download authorization and large-upload behaviour; document Cold
  retrieval charges and 90-day minimum billing in operational settings.
- [ ] Use the chosen region/LRS baseline; add no operational backup objects or
  automatic transcoding. Coordinate expired-object policy with editor trash.

## Verification

Use real Azure storage to upload/read with managed-identity-issued access,
validate anonymous/CORS boundaries, change one separate original to Cold and
continue Hot playback. Test an ineligible shared original/playback object and failure.

## Handoff and parallel work

Supply actual tier/capacity measurements to ARC-041/042. Coordinate media-resource
changes with extraction/import jobs. Basic live Blob access is already delivered
by ARC-049, so those jobs do not depend on this recording-tier feature.
