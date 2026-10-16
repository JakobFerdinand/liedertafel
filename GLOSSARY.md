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
