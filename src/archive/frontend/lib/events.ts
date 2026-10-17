// ARC-024: Auftritte des Chores mit ehrlicher Datumsdarstellung. Die
// Abrufhelfer folgen dem Muster der Lieder (deutsche Fehler als geworfene
// Antworten, keine Zwischenspeicherung).
// ARC-025/#171: Dokumente und Fotografien eines Auftritts sind Dateien und
// nutzen den Upload aus lib/dateien.ts.

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

// ─── ARC-026: Programm des Auftritts ─────────────────────────────────
// Ein Programm gehört zu genau einem Auftritt; ein Programmpunkt verweist
// auf ein Lied (#170). Dasselbe
// Lied darf doppelt stehen — zwei eigene Einträge mit eigener stabiler
// Programmpunkt-Id. Vor der ersten Veröffentlichung sehen Mitglieder
// nichts (das eingebettete Programm bleibt null).

export type ProgrammPunkt = {
  // Stabile Id innerhalb eines Arbeitsstandes: unveränderte Einträge
  // behalten sie über ein Speichern hinweg (ganzer geordneter Ersatz).
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
  // ARC-029: die bestätigte Aufführung (was tatsächlich gesungen wurde),
  // getrennt vom Plan; null bis zur ersten Bestätigung.
  confirmation?: BestaetigungEmbed | null;
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

/** Liedseite eines Liedes. */
export function liedUrl(liedId: string): string {
  return `/lied/?id=${encodeURIComponent(liedId)}`;
}

// ─── ARC-028: Aufführungsnachweise ───────────────────────────────────
// Nachweise sammeln die überlieferten Belege eines Auftritts: eine
// bestätigte Aufführung („confirmed“) oder nur ein unverifizierter
// Programmhinweis („mention“). Ein Nachweis nennt ein Lied (#170). Die Einbettung liest sich aus den
// Auftrittsdetails hinter `programme`; ältere Antworten ohne das Feld
// bleiben zulässig.

export type Nachweis = {
  id: string;
  liedId: string;
  liedTitle: string | null;
  evidenceStatus: string;
  position: number;
};

// Redaktionseinbettung: dieselbe Zeile plus Ersatzangabe, Erfassungs-
// und Änderungszeitpunkt, Eignerereignis und Concurrency-Marke. Mitglieder
// erhalten (vertragsgemäß) nur die schlanke Zeile oben — ohne Quellenangabe.
export type NachweisEditor = Nachweis & {
  eventId: string;
  // ARC-029: gehört zur Programmbestätigung (wird dort geändert).
  programmeItemId?: string | null;
  confirmationId?: string | null;
  sourceNote: string | null;
  capturedAt: string;
  createdAt: string;
  updatedAt: string;
  rowVersion: number;
};

/** Übersetzt die Auftrittsdetails auf den Nachweis-Einbettungstyp;
    ältere Antworten bleiben zulässig (Toleranz wie bei programme). */
export type AuftrittDetailsMitBelegen = AuftrittDetails & {
  performances?: Nachweis[] | null;
};

/** Körper des Erfassungsaufrufs: leere Quellenangaben werden weggelassen. */
export type NachweisErfassung = {
  liedId: string;
  evidenceStatus: string;
  sourceNote?: string;
  // Wiederholungsschlüssel gegen verlorene Doppelklicks: derselbe
  // Schlüssel liefert denselben Nachweis zurück, statt neu anzulegen.
  idempotencyKey?: string;
};

/** PATCH-Körper: abwesend = unverändert, explizit null räumt ab
    (sourceNote null die Angabe). */
export type NachweisAenderung = {
  evidenceStatus?: string;
  sourceNote?: string | null;
  rowVersion: number;
};

/**
 * Hängt einen neuen Nachweis an den Auftritt; der Server antwortet mit
 * der vollständigen Redaktionseinbettung (201, nach einer Wiederholung
 * mit demselben Schlüssel mit 200).
 */
export async function posteNachweis(
  eventId: string,
  body: NachweisErfassung,
): Promise<NachweisEditor> {
  const response = await postAuth(
    `/api/events/${encodeURIComponent(eventId)}/performances`,
    body,
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { performance: NachweisEditor };
  return data.performance;
}

/**
 * Ändert Nachweisart und/oder Quellenangabe (PATCH:
 * abwesende Felder bleiben unverändert). Ein veralteter rowVersion-Stand
 * antwortet mit 409 — der frische Stand kommt dann über den Eltern-Nachlauf.
 */
export async function aendereNachweis(
  id: string,
  body: NachweisAenderung,
): Promise<NachweisEditor> {
  const response = await patchAuth(
    `/api/performances/${encodeURIComponent(id)}`,
    body,
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { performance: NachweisEditor };
  return data.performance;
}

/** Entfernt den Nachweis ersatzlos (ohne rowVersion, bestätigte Redaktionsarbeit). */
export async function loescheNachweis(id: string): Promise<void> {
  const response = await postAuth(
    `/api/performances/${encodeURIComponent(id)}/delete`,
    {},
  );
  if (!response.ok) throw response;
}

// ─── ARC-029: tatsächlich gesungenes Programm ────────────────────────
// Die Redaktion bestätigt nach dem Auftritt eine veröffentlichte
// Programmrevision: Programmpunkte gesungen („sung“) oder ausgefallen
// („skipped“), mit korrigierter Fassung, dazu zusätzlich gesungene Lieder
// (Zugaben). Das Ergebnis liegt als bestätigte Aufführungsnachweise vor;
// der Plan bleibt unverändert. Mitglieder lesen die Einbettung
// `programme.confirmation` und sehen Plan und tatsächlichen Verlauf
// getrennt.

export type BestaetigungLied = {
  liedId: string;
  liedTitle: string | null;
  voiceConfiguration: string | null;
  musicalKey: string | null;
};

// Ein tatsächlich gesungenes Lied im Lesesaal (Mitglieder und Redaktion).
export type BestaetigungIst = BestaetigungLied & {
  performanceId: string;
  programmeItemId: string | null;
  // Zusätzlich zum Plan gesungen (Zugabe).
  added: boolean;
  evidenceStatus: string;
};

// Geplant, aber nicht gesungen.
export type BestaetigungAusgefallen = BestaetigungLied & {
  programmeItemId: string;
  position: number;
};

export type BestaetigungEmbed = {
  revisionId: string;
  revisionNumber: number;
  confirmedAt: string;
  updatedAt: string;
  // Die Bestätigung bezieht sich auf die zuletzt veröffentlichte Revision.
  upToDate: boolean;
  // Nur die Redaktion erhält die Concurrency-Marke.
  rowVersion?: number;
  actual: BestaetigungIst[];
  skipped: BestaetigungAusgefallen[];
};

export type BestaetigungAusgang = "open" | "sung" | "skipped" | "unconfirmed";

export type BestaetigungNachweis = BestaetigungLied & {
  id: string;
  evidenceStatus: string;
  // Redaktionsansicht: eine am Nachweis hinterlegte Quellenangabe.
  sourceNote: string | null;
  programmeItemId: string | null;
  rowVersion: number;
  // Zeitmarken in Aufnahmen, die an dieser Aufführung hängen (ARC-032):
  // solange es welche gibt, kann sie weder ausgelassen noch gelöscht werden.
  passageCount: number;
};

export type BestaetigungPunkt = BestaetigungLied & {
  programmeItemId: string;
  position: number;
  note: string | null;
  outcome: BestaetigungAusgang;
  performance: BestaetigungNachweis | null;
  // Nach einer Neuveröffentlichung: bisheriger Nachweis desselben Liedes,
  // den die Redaktion ausdrücklich übernehmen kann.
  suggestedPerformanceId: string | null;
  // Dieser Vorschlag samt Marke; er kann ein früherer
  // Nachweis (nach einer Neuveröffentlichung) oder ein von Hand erfasster
  // Nachweis desselben Liedes sein.
  suggestedPerformance: BestaetigungNachweis | null;
};

export type BestaetigungPruefung = {
  revision: { id: string; number: number; publishedAt: string };
  currentRevisionId: string;
  upToDate: boolean;
  // false, solange der Auftritt noch sicher in der Zukunft liegt; der Grund
  // steht in unconfirmableReason.
  confirmable: boolean;
  unconfirmableReason: string | null;
  confirmation: {
    id: string;
    revisionId: string;
    revisionNumber: number | null;
    confirmedAt: string;
    updatedAt: string;
  } | null;
  // 0 solange nichts bestätigt wurde; Anker für die Concurrency.
  rowVersion: number;
  items: BestaetigungPunkt[];
  additions: BestaetigungNachweis[];
  // Von Hand erfasste Nachweise von Liedern des Programms, die in keiner
  // Bestätigung stehen und doppelt zählten, wenn sie nicht übernommen werden.
  unownedOccurrences: BestaetigungNachweis[];
};

export type BestaetigungEintrag = {
  programmeItemId: string;
  outcome: "sung" | "skipped";
  performanceId?: string;
  rowVersion?: number;
};

export type BestaetigungZusatz = {
  performanceId?: string;
  // Client-Schlüssel gegen Doppelanlage: derselbe Schlüssel liefert
  // denselben Nachweis zurück.
  clientKey?: string;
  liedId: string;
  rowVersion?: number;
};

export type BestaetigungAbsenden = {
  revisionId: string;
  rowVersion: number;
  items: BestaetigungEintrag[];
  additions: BestaetigungZusatz[];
  // Jeder geladene Nachweis mit seiner Zeilenmarke: entfernt die Aussage
  // einen davon, muss er unverändert sein (sonst 409).
  knownOccurrences?: Array<{ performanceId: string; rowVersion: number }>;
};

/** Prüfansicht einer veröffentlichten Revision (ohne Angabe: die neueste). */
export async function fetchBestaetigung(
  eventId: string,
  revisionId?: string,
  signal?: AbortSignal,
): Promise<BestaetigungPruefung> {
  const query = revisionId
    ? `?revisionId=${encodeURIComponent(revisionId)}`
    : "";
  const response = await fetch(
    `/api/events/${encodeURIComponent(eventId)}/programme/confirmation${query}`,
    { credentials: "same-origin", cache: "no-store", signal },
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { review: BestaetigungPruefung };
  return data.review;
}

/**
 * Bestätigt das tatsächliche Programm als ganze, wiederholbare Aussage:
 * dieselben Nachweise werden fortgeschrieben, eine Wiederholung ändert
 * nichts. Veraltete Revisionen und Stände antworten mit 409.
 */
export async function putBestaetigung(
  eventId: string,
  body: BestaetigungAbsenden,
): Promise<BestaetigungPruefung> {
  const response = await putAuth(
    `/api/events/${encodeURIComponent(eventId)}/programme/confirmation`,
    body,
  );
  if (!response.ok) throw response;
  const data = (await response.json()) as { review: BestaetigungPruefung };
  return data.review;
}
