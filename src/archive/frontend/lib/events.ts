// ARC-024: Auftritte des Chores mit ehrlicher Datumsdarstellung. Die
// Abrufhelfer folgen dem Muster der Lieder (deutsche Fehler als geworfene
// Antworten, keine Zwischenspeicherung).
// ARC-025: an einem Auftritt hängende Dokumente und Fotografien nutzen
// denselben Drei-Schritte-Upload wie die Noten (Sitzung, Übertragung zum
// Ticket, Abschluss); die Helfer hier formen nur die Ereignis-spezifische
// Vertragssprache (Arten, Beschreibung statt Stimme).

import { patchAuth, postAuth, putAuth } from "@/lib/auth";

// Geschlossene Artenmenge des Vertrags; die deutschen Namen liest die
// Oberfläche daraus.
export type AuftrittArt =
  | "concert"
  | "service"
  | "wedding"
  | "funeral"
  | "festival"
  | "other";

export type Auftritt = {
  id: string;
  kind: string;
  title: string;
  venue: string | null;
  dateYear: number | null;
  dateMonth: number | null;
  dateDay: number | null;
  dateApproximate: boolean;
  // day = genaues Datum, month = Monat bekannt, year = nur das Jahr,
  // unknown = nichts überliefert (kein erfundenes Kalenderdatum).
  datePrecision: "day" | "month" | "year" | "unknown";
  // Serverseitig geformte deutsche Anzeige: „12. Mai 1950“, „um 1950“
  // oder „Datum unbekannt“.
  dateDisplay: string;
  startTime: string | null;
  published: boolean;
};

// ARC-025: Dokumente und Fotografien am Auftritt. „document" ist ein
// eingescanntes Programm oder Schriftstück (PDF), „photo" eine historische
// Fotografie (JPEG/PNG/WEBP). Die Beschreibung steht als sichtbare
// Bildunterschrift und dient zugleich als Vorlesetext (alt) des Bildes.
export type DokumentTyp = "document" | "photo";

// Dieselbe Revisionsangabe wie bei den Noten (lib/songs.ts): Fassung,
// Inhaltstyp und Größe des hochgeladenen Standes. Ohne abgeschlossenen
// Upload bleibt currentRevision null – der Eintrag ist dann noch nicht
// hochgeladen und trägt keine Tickets.
export type DokumentRevision = {
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
};

export type Dokument = {
  id: string;
  assetType: DokumentTyp;
  description: string | null;
  createdAt: string;
  currentRevision: DokumentRevision | null;
};

// Antwortform der Anlage- und Änderungsaufrufe (Asset-Eignervertrag):
// dieselben Felder wie Dokument, dazu die Eigner-Kennzeichnung.
export type EventAssetAntwort = Dokument & {
  eventId: string | null;
  musicalVersionId: string | null;
  voiceLabel: string | null;
};

export type AuftrittDetails = Auftritt & {
  notes: string | null;
  sourceNote: string | null;
  documents: Dokument[];
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
};

// Die Jahreszusammenfassung der ganzen sichtbaren Menge für die
// Jahresleiste; Auftritte ohne Datum laufen unter year: null.
export type AuftrittJahr = { year: number | null; count: number };

export type AuftrittSuchErgebnis = {
  years: AuftrittJahr[];
  total: number;
  events: Auftritt[];
};

export const auftrittArten: Record<string, string> = {
  concert: "Konzert",
  service: "Gottesdienst",
  wedding: "Hochzeit",
  funeral: "Bestattung",
  festival: "Fest",
  other: "Sonstiger Auftritt",
};

export function auftrittArtName(kind: string): string {
  return auftrittArten[kind] ?? "Sonstiger Auftritt";
}

export function auftrittUrlPfad(auswahl: {
  year?: number | null;
  kind?: string | null;
}): string {
  const parameter = new URLSearchParams();
  if (typeof auswahl.year === "number") {
    parameter.set("year", String(auswahl.year));
  }
  const kind = auswahl.kind?.trim();
  if (kind) parameter.set("kind", kind);
  const zeichenkette = parameter.toString();
  return zeichenkette ? `/api/events?${zeichenkette}` : "/api/events";
}

export async function fetchEvents(
  auswahl: { year?: number | null; kind?: string | null } = {},
  signal?: AbortSignal,
): Promise<AuftrittSuchErgebnis> {
  const response = await fetch(auftrittUrlPfad(auswahl), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as AuftrittSuchErgebnis;
}

export async function fetchEvent(
  id: string,
  signal?: AbortSignal,
): Promise<AuftrittDetails> {
  const response = await fetch(`/api/events/${encodeURIComponent(id)}`, {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { event: AuftrittDetails };
  return data.event;
}

// Erlaubte Inhaltstypen je Materialart (Spiegel der Backend-Whitelist:
// Dokumente nur PDF, Fotografien JPEG/PNG/WEBP).
const erlaubteDokumentInhaltstypen: Record<DokumentTyp, string[]> = {
  document: ["application/pdf"],
  photo: ["image/jpeg", "image/png", "image/webp"],
};

const dokumentInhaltstypNachEndung: Record<string, string> = {
  pdf: "application/pdf",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png",
  webp: "image/webp",
};

export function istDokumentTyp(typ: string): typ is DokumentTyp {
  return typ === "document" || typ === "photo";
}

/** Vorwahl der Materialart aus dem MIME-Typ; unbekanntes gilt als Dokument. */
export function dokumentTypFuerDatei(datei: File): DokumentTyp {
  const inhaltstyp = datei.type.toLowerCase();
  if (inhaltstyp.startsWith("image/")) return "photo";
  if (inhaltstyp === "application/pdf") return "document";
  const endung = datei.name.toLowerCase().split(".").pop() ?? "";
  if (
    endung === "jpg" ||
    endung === "jpeg" ||
    endung === "png" ||
    endung === "webp"
  ) {
    return "photo";
  }
  return "document";
}

/** Wirksamer Inhaltstyp der Übertragung: erkannter Typ, sonst Whitelist-Ersatz. */
export function contentTypeFuerDokument(typ: DokumentTyp, datei: File): string {
  const erlaubt = erlaubteDokumentInhaltstypen[typ];
  const inhaltstyp = datei.type.toLowerCase();
  if (erlaubt.includes(inhaltstyp)) return inhaltstyp;
  const endung = datei.name.toLowerCase().split(".").pop() ?? "";
  const nachEndung = dokumentInhaltstypNachEndung[endung];
  if (nachEndung && erlaubt.includes(nachEndung)) return nachEndung;
  return erlaubt[0];
}

/** Deutscher Name der Materialart für Zeilen und Etiketten. */
export function dokumentTypName(typ: DokumentTyp): string {
  return typ === "photo" ? "Fotografie" : "Dokument";
}

/**
 * ARC-025: hängt ein neues Dokument/eine neue Fotografie an den Auftritt.
 * Der Aufruf legt nur den Eigner-Eintrag an; am Auftritt selbst ändert
 * sich nichts (kein Programm, keine bestätigte Aufführung).
 */
export async function createEventAsset(
  eventId: string,
  body: { assetType: DokumentTyp; description?: string },
): Promise<EventAssetAntwort> {
  const response = await postAuth(
    `/api/events/${encodeURIComponent(eventId)}/assets`,
    body,
  );
  if (!response.ok) throw response;
  return (await response.json()) as EventAssetAntwort;
}

/**
 * Ändert Materialart und/oder Beschreibung eines Auftritts-Materials
 * (PATCH: abwesende Felder bleiben unverändert). Der Typ bleibt nach dem
 * ersten Hochladen gesperrt; der Server antwortet dann mit 409.
 */
export async function patchEventAsset(
  assetId: string,
  body: { assetType?: DokumentTyp; description?: string | null },
): Promise<EventAssetAntwort> {
  const response = await patchAuth(
    `/api/assets/${encodeURIComponent(assetId)}`,
    body,
  );
  if (!response.ok) throw response;
  return (await response.json()) as EventAssetAntwort;
}

// ─── ARC-026: Programm des Auftritts ─────────────────────────────────
// Ein Programm gehört zu genau einem Auftritt; ein Programmpunkt verweist
// auf eine Liedfassung (Arrangement + musikalische Version). Dasselbe
// Lied darf doppelt stehen — zwei eigene Einträge mit eigener stabiler
// Programmpunkt-Id. Vor der ersten Veröffentlichung sehen Mitglieder
// nichts (das eingebettete Programm bleibt null).

export type ProgrammPunkt = {
  // Stabile Id innerhalb eines Arbeitsstandes: unveränderte Einträge
  // behalten sie über ein Speichern hinweg (ganzer geordneter Ersatz).
  id: string;
  position: number;
  songId: string;
  arrangementId: string;
  musicalVersionId: string;
  // Anzeigefelder aus der Kette; Unbekanntes bleibt null (keine
  // erfundenen Plätze).
  songTitle: string | null;
  arrangementLabel: string | null;
  voiceConfiguration: string | null;
  musicalVersionLabel: string | null;
  musicalKey: string | null;
  note: string | null;
};

export type ProgrammRevision = {
  id: string;
  number: number;
  updatedAt: string;
  items: ProgrammPunkt[];
};

export type ProgrammRevisionVeroeffentlicht = {
  id: string;
  number: number;
  publishedAt: string;
  items: ProgrammPunkt[];
};

// Eingebettetes Programm der Auftrittsdetails. Mitglieder erhalten nur
// die neueste veröffentlichte Revision (working null), die Redaktion
// zusätzlich den Entwurf und die eingefrorenen früheren Fassungen
// (history, älteste zuerst, ohne die neueste); vor der ersten
// Veröffentlichung bleibt membersichtbar alles null. Ältere Antworten
// ohne das Feld bleiben zulässig.
export type ProgrammEmbed = {
  id: string;
  rowVersion: number;
  working: ProgrammRevision | null;
  published: ProgrammRevisionVeroeffentlicht | null;
  // ARC-027: Verlaufsrevisionen für die Werkbank; Mitglieder erhalten null.
  history?: ProgrammRevisionVeroeffentlicht[] | null;
};

/** Übersetzt die Auftrittsdetails auf den Programm-Einbettungstyp;
    ältere Antworten bleiben zulässig (Toleranz wie bei documents). */
export type AuftrittDetailsMitProgramm = AuftrittDetails & {
  programme?: ProgrammEmbed | null;
};

export type ProgrammListeZeile = {
  eventId: string;
  eventTitle: string;
  kind: string;
  dateDisplay: string;
  datePrecision: "day" | "month" | "year" | "unknown";
  dateApproximate: boolean;
  venue: string | null;
  startTime: string | null;
  publishedAt: string;
  itemCount: number;
};

export type ProgrammListe = {
  programmes: ProgrammListeZeile[];
};

/**
 * Ganzer geordneter Ersatz des Entwurfs: die Reihenfolge der Liste setzt
 * die Positionen 1..n, Einträge mit bekannter Programmpunkt-Id bleiben
 * im Wesen erhalten (stabile Ids), fehlende Ids fallen weg. Ohne Programm
 * legt der erste PUT es still an (rowVersion entfällt); bei vorhanden
 * Programm zählt sie als Concurrency-Anker. Der Server antwortet mit der
 * frischen Programmeinbettung (neue rowVersion).
 */
export async function putProgrammItems(
  eventId: string,
  body: {
    items: Array<{
      id?: string;
      songId: string;
      musicalVersionId: string;
      note?: string;
    }>;
    rowVersion?: number;
  },
): Promise<ProgrammEmbed> {
  const response = await putAuth(
    `/api/events/${encodeURIComponent(eventId)}/programme/items`,
    body,
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { programme: ProgrammEmbed };
  return data.programme;
}

/** Veröffentlicht den aktuellen Entwurf als Mitgliedssichtbare Revision. */
export async function publishProgramm(
  eventId: string,
  rowVersion: number,
): Promise<ProgrammEmbed> {
  const response = await postAuth(
    `/api/events/${encodeURIComponent(eventId)}/programme/publish`,
    { rowVersion },
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { programme: ProgrammEmbed };
  return data.programme;
}

/** Kommende veröffentlichte Programme (serverseitig sortiert). */
export async function fetchProgramme(
  signal?: AbortSignal,
): Promise<ProgrammListeZeile[]> {
  const response = await fetch("/api/programmes", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as ProgrammListe;
  return data.programmes ?? [];
}

/**
 * Deutsche Uhrzeit der Veröffentlichung in Wien (wie das backendgeformte
 * dateDisplay); ohne explizite Zeitzone liest der Browser sie nach der
 * örtlichen Zone — die Anzeige bleibt ehrlich, es wird nichts gerundet.
 */
export function publishedAtText(zeitstempel: string): string {
  const zeit = new Date(zeitstempel);
  if (Number.isNaN(zeit.getTime())) return "—";
  return zeit.toLocaleString("de-AT", {
    day: "numeric",
    month: "long",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: "Europe/Vienna",
  });
}

/**
 * Tief verlinkte Liedseite des Programmpunkts: das Mitglied landet direkt
 * bei der Liedfassung und musikalischen Version des Eintrags.
 */
export function programmPunktUrl(punkt: ProgrammPunkt): string {
  const parameter = new URLSearchParams();
  parameter.set("id", punkt.songId);
  parameter.set("fassung", punkt.arrangementId);
  parameter.set("version", punkt.musicalVersionId);
  return `/lied/?${parameter.toString()}`;
}
