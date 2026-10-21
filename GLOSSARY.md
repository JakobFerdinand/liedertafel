# Liedertafel Archive

The private, German-language choir archive (`src/archive`): Lieder with their
Dateien, and the Auftritte at which they were sung. Canonical names are the
German UI terms; English equivalents are given for code and issues.

## Language

**Lied** (song):
One playable piece as the choir sings it: one arrangement, one voicing, one key.
A different arrangement or key of the same music is a separate Lied with its own
title.
_Avoid_: Arrangement, Fassung, musical version, Bearbeitung (as an entity)

**Datei** (file):
A file attached to a Lied or an Auftritt; a Lied usually has several. Replacing a
Datei replaces it; there is no revision history.
_Avoid_: Asset, file revision, Revision

**Noten** (score):
A PDF Datei of the written music, either the Partitur or one Stimme.
_Avoid_: Notenblatt, sheet

**Partitur** (full score):
Noten containing all Stimmen together.

**Stimme** (voice part):
One vocal line of a Lied, such as "Tenor 1" or "Bass 2"; Noten and Übe-Dateien
can be for a single Stimme.
_Avoid_: Part, voice track

**Übe-Datei** (practice file):
Audio or MIDI made for rehearsing, usually for one Stimme or "Alle".
_Avoid_: Practice track, Übungsdatei

**Aufnahme** (recording):
Audio or video of the choir actually singing. It belongs either to a Lied (that
Lied alone, optionally naming the Auftritt it came from) or to an Auftritt (the
whole appearance, usually a video).
_Avoid_: Mitschnitt, concert recording (as the general term)

**Zeitmarke** (time marker):
The point in an Auftritt's Aufnahme where one Lied of its Programm begins.
_Avoid_: Marker, passage, Sprungmarke

**Auftritt** (appearance, event):
A dated occasion at which the choir sang, past or upcoming; historical dates may
be known only approximately.
_Avoid_: Event, Konzert (as the general term)

**Programm** (programme):
The ordered list of Lieder for an Auftritt. It is also the record of what was
sung; it is edited afterwards if the actual order differed.
_Avoid_: Programme revision, confirmation, performance

**Entwurf** (draft):
The form of a new Lied or a new Auftritt while Dateien are being dropped and
filled in, before it exists. It is the only unfinished state; it holds the form
(for an Auftritt also the Programm with the Lied chosen for each entry), which
fields the editor touched and the Dateien without their bytes, never where a
value came from. It ends when "Speichern" has uploaded every Datei, or when it
is discarded.
_Avoid_: Draft Lied, Entwurfs-Lied, Vorlage

**Angefangen** (started, unfinished):
The list on the Verwaltung page of the editor's own Entwürfe, to resume or
discard. A Datei that failed to upload stays in its Entwurf here until it is
retried, removed or the Entwurf is discarded.
_Avoid_: Unfertig, Warteschlange

**Lesung** (reading):
One request to read a PDF of an Entwurf (embedded text first, a scan through the
model) or, for an Auftritt, a programme scan or photo into suggestions for the
form. It never writes a Lied or an Auftritt: a suggestion fills only a field the
editor has not touched and is marked until edited or saved. For an Auftritt it
also matches each Programm entry against the Lieder; a Lied is only offered or
pre-selected, the editor decides.
Discarding the Entwurf, removing the Datei, or a newer request for the same Datei
stops it.
_Avoid_: Extraktion (that is the text step of a stored Datei), Analyse
