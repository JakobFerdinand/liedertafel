# Liedertafel Choir Archive — Product Requirements Document

Status: User-confirmed feature baseline, aligned with the architecture interview;
implementation has not started.
Date: 2026-09-11

This plan records the outcome of the feature interview and the user's explicit
confirmation. The [architecture and deployment plan](architecture.md) records
the subsequent technical decisions and supersedes the original architecture
proposal supplied in conversation on 2026-09-08.

The [implementation issue index](issues.md) breaks this agreement into small
deliverables with explicit dependencies and parallel-work guidance.

## 1. Product goal

Build a private, German-language music archive for current choir singers,
conductors, and accompanists. Its main value is connecting songs, the correct
musical materials, and documented performance history.

The two most important problems are:

1. Scores, practice files, and recordings exist, but their relationships are
   difficult to follow.
2. It is difficult to establish when a piece was performed and find the relevant
   recording passage.

The archive also holds authoritative published programmes for upcoming choir
appearances. A small, trusted editor team maintains the collection.

### Existing collection

- Approximately 500 songs/arrangements; this is a combined estimate, not a count
  of 500 distinct songs plus their arrangements.
- Approximately 30 years of recordings and 120 years of choir history.
- Digital scores, paper scores, practice files, concert recordings, and
  historical programme records.
- A substantial Google Drive catalogue organized in folders per song or
  arrangement, intended for a one-time migration.

The architecture interview estimated 100–500 GB of source material, with original
files up to approximately 10 GB each. Actual folder conventions, file counts,
recording formats, storage volume, and historical source quality still need
inspection.

## 2. Audience, access, and devices

### Members

- Current singers, conductors, and accompanists use one shared member-visible
  collection.
- Each person has an individual account, admitted through an administrator's
  email invitation, with app-owned email-code sign-in.
- Personal-device sessions can remain signed in for 30 days; server-side
  membership and role checks still apply, and explicit logout is available.
- Archive links shared between members require sign-in.
- Browsing, search, score viewing, and playback must work comfortably on phones
  and tablets during ordinary online use.

### Editors

- A small editor team independently creates, corrects, and publishes catalogue
  content, files, programmes, historical records, and recording markers.
- Heavy catalogue work and recording indexing are optimized for computers.
- Useful partial records can be published and completed gradually.
- Changes retain contributor attribution; ordinary deletion is recoverable in
  the editor trash for seven days.

### Administrators

- Invite members, resend invitations, manage roles, and deactivate/reactivate
  membership.
- Help correct account email addresses after checking identity.
- Control permanent deletion.
- Deactivation blocks subsequent authorized archive/media-access requests while
  preserving contribution history. Already issued short-lived media links
  expire naturally.

The app owns sign-in and uses Azure Communication Services Email for delivery.
A restricted maintainer command supports administrative account repair. Archive
roles remain separate from the existing analytics dashboard's access controls.

## 3. Catalogue model and file identity

The member-facing hierarchy is:

**Song → Arrangement → Musical version/key → Matching materials**

- A song has a primary title, optional alternate titles, creator information,
  and optional entered lyrics/opening words.
- Multiple arrangements of the same song appear together on its song page,
  with clearly separated materials and history.
- An arrangement can have labelled musical versions, such as transpositions
  into different keys. Each version groups matching scores and practice files.
- Programme entries identify the intended arrangement and musical version.
- A correction to an existing PDF is a **file revision**, distinct from a new
  arrangement or musical version.
- Members see current files by default. Editors can inspect and recover earlier
  file revisions.
- Saved score revisions remain live catalogue content until explicitly removed;
  the seven-day editor-trash window is not automatic expiry of revision history.
- Historical events link to the chosen musical version's current materials;
  exact concert-specific file snapshots are not a launch requirement.
- Historical records can identify a song while leaving its arrangement unknown.

### Metadata and search

Search supports:

- Primary and alternate titles.
- Composer, arranger, and lyricist information.
- Entered lyrics and opening words.
- Extractable text already embedded in digital PDFs.

Catalogue filters support:

- Voice configuration, including different choir configurations and divided
  parts where relevant.
- Available materials: scores, practice audio, MIDI, and recordings.
- Occasion, language, and tags.
- Accompaniment/instrumentation.
- Musical key.

Metadata can be incomplete. Unknown values must remain explicit rather than
being guessed to satisfy required fields. Tags supplement structured musical
relationships and metadata.

## 4. Member navigation

- A search-first home provides prominent search and a browsable song catalogue.
- Upcoming published programmes are readily accessible from the home screen.
- A separate historical event view provides access to past choir appearances.
- A song page brings together its arrangements, musical versions, materials,
  upcoming programme appearances, confirmed performances, historical programme
  evidence, and linked recording passages.
- Results must lead to the matching arrangement/material rather than leaving
  members to infer relationships from filenames alone.

## 5. Materials and playback

### Scores and practice files

- Members view scores in the app.
- Files are grouped by purpose/type and clearly labelled with voice part or
  full-mix information. Members choose the desired part manually.
- Editors can upload multiple files into an arrangement or event workflow and
  assign types, descriptions, and voice labels together.
- Audio and video support basic in-app playback and seeking.
- MIDI supports in-app play/pause, seeking, volume, and tempo adjustment using
  basic synthesized playback.

### Concert recordings

- A whole-concert recording can become member-visible before song indexing is
  complete.
- While watching/listening, editors manually capture song start/end positions
  and associate them with the relevant performance/programme entry.
- Members can jump from a song's performance history to its recording passage.
- Several recordings, such as a camera video and separate audio recording, can
  document the same performance. They do not create additional performances.
- Preserve original recordings. If a format cannot play in supported browsers,
  an editor can attach an externally converted playback copy.
- Other published catalogue/history content remains useful while a playable
  copy or recording markers are pending.
- Large-file uploads provide progress and retryable/chunked transfer. Playback
  uses renewable 15-minute file-access tickets so long recordings remain usable
  while membership is rechecked for renewed access.

### Downloads

- Scores, MIDI, and practice audio are downloadable by members.
- Concert recordings play in the app; their downloads require explicit editor
  enablement.
- Archive-page links require membership, including when shared by another
  member.
- Download controls describe the offered interface/access policy; playback is
  not copy protection.

## 6. Events and published programmes

### Event scope

Events include concerts, church services, weddings, funerals, festivals, and
other choir appearances. They carry date/time/place and member-visible notes,
including practical details such as meeting time or clothing where useful.

Historical programmes, documents, and photographs can be attached to events.
This is event-centred choir history rather than an independent collection of
historical people, correspondence, or museum-style objects.

### Programme workflow

1. An editor builds an ordered programme with the intended arrangements and
   musical versions.
2. The editor explicitly publishes it for members.
3. Later edits take place in a working revision. Members continue to see the
   previously published programme until the revision is published.
4. The member-visible programme shows when it was last updated and is the
   authoritative programme in the archive.
5. After the event, an editor confirms or corrects the actual programme,
   including skipped songs and additions. Bulk confirmation is possible when
   the planned programme was performed unchanged.

The passage of an event date alone does not turn planned appearances into
confirmed performances. Planned and actual programmes remain distinguishable.

## 7. Historical evidence and counts

- Support partial and approximate historical dates, including records known
  only by year or approximate period.
- Preserve source notes and allow unknown arrangements.
- A historical printed programme is evidence of a programme appearance; it is
  not automatically proof that every listed song was performed.
- Show confirmed performances separately from documented but unconfirmed
  programme appearances, both in history and numerical summaries.
- Qualify counts as based on recorded history, rather than suggesting complete
  coverage of the choir's existence.
- Multiple recordings or other evidence for one performance must not inflate
  its count.
- Useful historical records can be published despite explicitly identified gaps.

## 8. Google Drive migration

The migration produces an independent archive containing its own copies of
selected source files and catalogue information. Future editing happens in the
archive.

### Required migration behaviour

1. Inspect representative source folders before defining inference rules.
2. Propose songs, arrangements, musical versions, and file associations based
   on the actual folder/file conventions.
3. Preview proposed records and preserve source paths/references and existing
   catalogue numbers where present.
4. Allow editors to bulk-accept clear matches.
5. Route ambiguous folders, files, or relationships to an editor-only review
   queue; do not silently invent musical identities or overwrite uncertain
   versions.
6. Make setup imports safely rerunnable without duplicating records or files.
7. Resolve migration duplicates before publishing affected records, even though
   the full general-purpose merge interface follows the first release.

Useful, reviewed records can be published with missing optional metadata.
Unsorted or ambiguous imported material remains editor-only.

## 9. Release phases

### First member release: core archive

- Individual invited membership, roles, and membership administration.
- Linked song/arrangement/musical-version catalogue and current materials.
- Search, filters, entered lyric search, and extractable PDF text search.
- Mobile-friendly score viewing and audio/video/MIDI playback.
- Downloads according to the agreed media-type policy.
- Event-centred history with explicit uncertainty and separate evidence/counts.
- Whole recordings, multiple recordings per performance, and manual markers.
- Ordered future programmes, explicit publication/revisions, and confirmation
  of what actually happened.
- Trusted-editor workflows, multi-file upload, file revision recovery,
  contributor attribution, and recoverable deletion.
- Reviewed one-time Drive migration and resolution of migration duplicates.

### Follow-up: editorial conveniences

**In-app duplicate merging:** Editors preview and merge duplicate songs or
arrangements while preserving materials, programme/performance links, recording
links, source references, and evidence. Ambiguous versions are not silently
overwritten, and merges must not double-count performances.

**Member correction inbox:** Members submit corrections or missing material
against a song, event, or recording, optionally with an attachment. Editors
review and resolve submissions; submissions do not automatically alter the
published catalogue.

### Grounded German chatbot (next priority)

- Read-only questions over the same member-authorized archive data.
- Answers link to supporting archive records.
- Confirmed performances and uncertain historical evidence remain distinct.
- A representative use case is: “Wann haben wir dieses Lied gesungen?”

### Features outside the agreed first release

- OCR of scanned or handwritten scores/documents.
- MIDI track mixing/soloing, automatic MIDI-to-audio generation, practice loops,
  progress tracking, and offline rehearsal functionality.
- Automatic media conversion or automatic recording boundary detection.
- Continuous synchronization with Google Drive and in-app paper scanning.
- Rehearsal planning, collaborative repertoire proposals, and guest sharing.
- Duration/difficulty filters, programme sections/breaks, print/export bundles,
  private programme notes, and change notifications.
- Full historical-object/person cataloguing outside events.
- Special workflows for unknown-event recordings, cross-event compilations, or
  split performances across multiple passages/files.

These were not selected as first-release requirements. Future additions should
be explicit scope decisions rather than inferred from the architecture.

Operational database/media backups and disaster-restoration workflows were
explicitly removed during the architecture interview. This does not remove
ordinary score revision history or seven-day editor-trash recovery, which are
part of the live application.

## 10. Content launch gate

Launch requires:

1. The restricted production infrastructure pilot described in the
   [architecture validation gates](architecture.md#11-validation-gates) has
   passed using maintainer accounts and a small representative dataset.
2. The usable digital Drive catalogue imported and reviewed, with incomplete
   metadata visibly allowed.
3. A representative selection of concerts across different years fully linked
   enough to demonstrate song → performance/evidence → recording passage.
4. A clearly incomplete remaining history/recording backlog that editors can
   enrich after members receive access.

Launch does not depend on manually indexing all 30 years of recordings or
reconstructing all 120 years of choir history. The exact pilot concerts and
representative source folders still need to be selected.

## 11. Acceptance scenarios

| Scenario | Expected outcome |
| --- | --- |
| A singer finds a song with two arrangements and two keys of one arrangement. | The singer can choose the intended arrangement/key and open matching scores and voice-labelled practice files. |
| A member searches using an alternate title, creator, entered opening line, or text embedded in a PDF. | Matching catalogue/material results lead to the appropriate song and arrangement. |
| A practice file exists only as MIDI. | The member can listen in the app, seek, and adjust tempo and volume. |
| A member asks when a song was sung. | History distinguishes confirmed performances from unconfirmed programme evidence and shows source context and date uncertainty. |
| One performance has both camera video and audio. | Both are reachable from that performance, which is counted once. |
| A whole-concert recording is published before indexing. | Members can play the whole recording; later manual markers enable direct song jumps. |
| An editor revises a published programme. | Members see the previous published list until explicit publication of the new revision. |
| A planned song is skipped and an encore added. | The editor records the actual programme; planned and performed history remain distinct. |
| An editor corrects a score after a concert. | Members receive the current score for the same musical version, including through old event links; editors can recover the prior file revision. |
| An editor accidentally deletes an item. | It remains recoverable from editor trash for seven days; retained score revisions are not automatically expired by that window. |
| A historical programme has an approximate year and unknown arrangement. | It can be published with explicit uncertainty and does not automatically become a confirmed performance. |
| An editor imports ambiguous Drive folders, then reruns the import. | Clear matches can be accepted, uncertain matches remain for review, sources are retained, and records/files are not duplicated. |
| A member is deactivated. | Subsequent archive/media authorization is denied while contribution history remains intact; existing short-lived media links expire naturally. |
| A concert recording plays longer than 15 minutes. | File access renews while membership remains valid, preserving playback position and seeking behaviour. |

## 12. Relationship to the architecture baseline

The [confirmed architecture](architecture.md) is the technical baseline: an
additional archive app in this repository, a React/TypeScript frontend served by
one ASP.NET Core application, Neon PostgreSQL metadata, private blob media, and
separate archive roles. It records Austria East as the preferred Azure region,
local development plus production, explicit releases, and the EUR 10 normal-month
budget target.

Local development uses Aspire AppHost to start the app, required dependencies and
implemented workers. Development OpenTelemetry logs, traces and metrics from the
API and C# workers export to the Aspire dashboard. This is part of the developer
workflow, while production retains the agreed Container Apps deployment.

The user chose no application-operated database/media backups or disaster
restoration. Earlier proposed recovery targets are superseded; editor revision
history and seven-day recoverable deletion remain feature requirements.

Feature planning adds requirements that the eventual detailed model and delivery
plan must account for:

- Musical versions within arrangements, distinct from asset revision history.
- Working and published programme revisions, and planned versus actual entries.
- Approximate dates, historical evidence, and explicit confirmation semantics.
- Member-visible partial records versus editor-only import review state.
- Preserved import provenance and safe reruns.
- Browser MIDI playback and extractable PDF text indexing in the core release.
- Original media plus optionally editor-supplied playback copies.
- Recoverable editing, followed by merges and contextual member submissions.

These are behavioural requirements, not a mandated final database schema.

### Factual and implementation follow-ups

- Inspect representative Drive folders, names, catalogue identifiers, duplicates,
  voice labels, and transposed/corrected files; define a concrete import mapping.
- Inventory recording counts, sizes, codecs, MIDI characteristics, and the amount
  of extractable PDF text versus scanned pages.
- Select representative historical sources and launch pilot concerts.
- Validate playback and score readability on the actual phone/tablet browsers
  members use, including MIDI and converted recording samples.
- Complete the architecture's explicit validation gates, including Austria East
  provisioning, Neon limits/provisioning, interrupted uploads, renewable playback
  access, cold starts, and the full monthly cost estimate.
- Validate app-owned invitation/email-code sign-in and actual email delivery,
  including restart-safe sessions and membership revocation.
- Verify the single-start Aspire developer environment and correlated development
  telemetry, including finite job completion and queue-based work.

Current provider checks, sources, and remaining factual uncertainties are recorded
in [architecture.md](architecture.md#13-sources-and-checks).
