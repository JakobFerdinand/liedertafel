// ARC-024: Auftritte des Chores mit ehrlicher Datumsdarstellung. Die
// Abrufhelfer folgen dem Muster der Lieder (deutsche Fehler als geworfene
// Antworten, keine Zwischenspeicherung).
// ARC-025/#171: Dokumente und Fotografien eines Auftritts sind Dateien und
// nutzen den Upload aus lib/dateien.ts.

import { postAuth, putAuth } from "@/lib/auth";

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
};

// ARC-025/#171: Dokumente und Fotografien am Auftritt sind Dateien des
// Auftritts. „document" ist ein eingescanntes Programm oder Schriftstück
// (PDF), „photo" eine historische Fotografie (JPEG/PNG/WEBP) – der Server
// unterscheidet sie am Inhaltstyp. Die Beschreibung steht als sichtbare
// Bildunterschrift und dient zugleich als Vorlesetext (alt) des Bildes.
export type DokumentArt = "document" | "photo";

export type Dokument = {
  id: string;
  art: DokumentArt;
  description: string | null;
  fileName: string | null;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  rowVersion: number;
};

export type AuftrittDetails = Auftritt & {
  notes: string | null;
  sourceNote: string | null;
  documents: Dokument[];
  createdAt: string;
  updatedAt: string;
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

/**
 * Legt einen Auftritt an (#131: "Speichern" des Formulars aus abgelegten
 * Dateien). Das Datum ergibt sich aus den ausgefüllten Feldern; leere Felder
 * bleiben ehrlich unbekannt.
 */
export async function legeAuftrittAn(werte: {
  kind: string;
  title: string;
  jahr: string;
  monat: string;
  tag: string;
  unsicher: boolean;
  venue: string;
}): Promise<AuftrittDetails> {
  const zahl = (text: string) =>
    text.trim() ? Number.parseInt(text.trim(), 10) : null;
  const response = await postAuth("/api/events", {
    kind: werte.kind,
    title: werte.title.trim(),
    venue: werte.venue.trim() || null,
    dateYear: zahl(werte.jahr),
    dateMonth: zahl(werte.monat),
    dateDay: zahl(werte.tag),
    dateApproximate: werte.unsicher,
  });
  if (!response.ok) throw response;
  return ((await response.json()) as { event: AuftrittDetails }).event;
}

const dokumentInhaltstypNachEndung: Record<string, string> = {
  pdf: "application/pdf",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png",
  webp: "image/webp",
};

/** Wirksamer Inhaltstyp einer gewählten Datei: erkannter Typ, sonst die Endung. */
export function contentTypeFuerDokument(datei: File): string {
  const inhaltstyp = datei.type.toLowerCase();
  if (Object.values(dokumentInhaltstypNachEndung).includes(inhaltstyp)) {
    return inhaltstyp;
  }
  const endung = datei.name.toLowerCase().split(".").pop() ?? "";
  return dokumentInhaltstypNachEndung[endung] ?? "application/pdf";
}

/** Deutscher Name der Materialart für Zeilen und Etiketten. */
export function dokumentArtName(art: DokumentArt): string {
  return art === "photo" ? "Fotografie" : "Dokument";
}

// ─── Programm des Auftritts (#160) ───────────────────────────────────
// Ein Auftritt hat genau ein Programm: eine geordnete, bearbeitbare Liste
// von Liedern, die zugleich festhält, was gesungen wurde. Speichern
// veröffentlicht es sofort (keine Entwürfe, Revisionen oder Bestätigung).
// Dasselbe Lied darf doppelt stehen — zwei eigene Einträge mit eigener
// stabiler Programmpunkt-Id, an der die Zeitmarken hängen.

export type ProgrammPunkt = {
  // Stabile Id: unveränderte Einträge behalten sie über ein Speichern
  // hinweg (ganzer geordneter Ersatz).
  id: string;
  position: number;
  liedId: string;
  // Anzeigefelder aus dem Lied; Unbekanntes bleibt null (keine
  // erfundenen Plätze).
  liedTitle: string | null;
  voiceConfiguration: string | null;
  musicalKey: string | null;
  note: string | null;
};

// Eingebettetes Programm der Auftrittsdetails; null, solange noch keines
// gespeichert wurde.
export type ProgrammEmbed = {
  id: string;
  rowVersion: number;
  updatedAt: string;
  items: ProgrammPunkt[];
};

// Aufnahme eines einzelnen Liedes, die diesen Auftritt nennt; sie steht
// auch in der Liedhistorie. Abspielen und Herunterladen laufen über die
// Dateien des Liedes.
export type AuftrittLiedAufnahme = {
  id: string;
  liedId: string;
  liedTitle: string;
  fileName: string | null;
  description: string | null;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
};

/** Auftrittsdetails mit Programm und Lied-Aufnahmen. */
export type AuftrittDetailsMitProgramm = AuftrittDetails & {
  programme?: ProgrammEmbed | null;
  liedAufnahmen?: AuftrittLiedAufnahme[];
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
  updatedAt: string;
  itemCount: number;
};

export type ProgrammListe = {
  programmes: ProgrammListeZeile[];
};

/**
 * Ganzer geordneter Ersatz des Programms: die Reihenfolge der Liste setzt
 * die Positionen 1..n, Einträge mit bekannter Programmpunkt-Id bleiben
 * erhalten (stabile Ids), fehlende Ids fallen weg. Ohne Programm legt der
 * erste PUT es an (rowVersion entfällt); bei vorhandenem Programm zählt sie
 * als Concurrency-Anker. Der Server antwortet mit dem frischen Programm.
 */
export async function putProgrammItems(
  eventId: string,
  body: {
    items: Array<{
      id?: string;
      liedId: string;
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

/** Kommende Programme (serverseitig sortiert). */
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

/** Liedseite eines Liedes. */
export function liedUrl(liedId: string): string {
  return `/lied/?id=${encodeURIComponent(liedId)}`;
}
