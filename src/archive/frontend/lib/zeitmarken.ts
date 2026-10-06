import { patchAuth, postAuth } from "@/lib/auth";

// ARC-032: markierte Stellen („Zeitmarken“) in ganzen Aufnahmen. Die Typen
// spiegeln die Antworten von /api/recordings/{id}/passages[/…] Feld für Feld.

/**
 * current: die Zeiten wurden an der Datei genommen, die Mitglieder jetzt
 * sehen · needsReview: die Datei hat gewechselt, die Zeiten sind ungeprüft.
 * Mitglieder erhalten bei needsReview keine Zeiten.
 */
export type ZeitmarkeZustand = "current" | "needsReview";

export type ZeitmarkeKette = { id: string; label: string };

export type Zeitmarke = {
  id: string;
  recordingId: string;
  performanceId: string;
  songId: string;
  songTitle: string;
  arrangement: ZeitmarkeKette | null;
  musicalVersion: ZeitmarkeKette | null;
  evidenceStatus: string;
  position: number;
  startSeconds: number | null;
  endSeconds: number | null;
  timestampState: ZeitmarkeZustand;
  /** Nur für die Redaktion. */
  editor: {
    version: number;
    playbackRevisionId: string;
    updatedAt: string;
  } | null;
};

/** Eine Aufführung des Auftritts in der Reihenfolge des Programms (nur Redaktion). */
export type Vorkommen = {
  performanceId: string;
  position: number;
  songId: string;
  songTitle: string | null;
  arrangement: ZeitmarkeKette | null;
  musicalVersion: ZeitmarkeKette | null;
  evidenceStatus: string;
  passageId: string | null;
};

export type ZeitmarkenStand = {
  recordingId: string;
  eventId: string;
  passages: Zeitmarke[];
  // Nur für die Redaktion.
  playbackRevisionId?: string | null;
  durationSeconds?: number | null;
  occurrences?: Vorkommen[];
  hasPublishedProgramme?: boolean;
};

/** Verknüpfung einer Aufführungszeile der Liedhistorie mit einer Aufnahme. */
export type HistorieAufnahme = {
  passageId: string;
  recordingId: string;
  recordingLabel: string;
  kind: "audio" | "video";
  isPublished: boolean;
  startSeconds: number | null;
  endSeconds: number | null;
  timestampState: ZeitmarkeZustand;
};

/** Vertrag RecordingPassageEndpoints.ConcurrencyMessage: veralteter Stand, neu laden. */
export const ZeitmarkeVeraltet =
  "Die Zeitmarke wurde zwischenzeitlich geändert.";

/**
 * Vertrag RecordingPassageEndpoints.StalePlaybackMessage: die Datei, die
 * Mitglieder abspielen, wurde ersetzt, seit die Ansicht gebaut wurde.
 */
export const ZeitmarkeDateiErsetzt =
  "Die Datei der Aufnahme wurde zwischenzeitlich ersetzt.";

/** Vertrag RecordingPassages.BlockedPrefix: Zeitmarken verhindern das Entfernen einer Aufführung. */
export const ZeitmarkenVorhanden = "Zeitmarken vorhanden:";

const basis = (recordingId: string) =>
  `/api/recordings/${encodeURIComponent(recordingId)}/passages`;

export async function fetchZeitmarken(
  recordingId: string,
  signal?: AbortSignal,
): Promise<ZeitmarkenStand> {
  const response = await fetch(basis(recordingId), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as ZeitmarkenStand;
}

export async function createZeitmarke(
  recordingId: string,
  body: {
    performanceId: string;
    startSeconds: number;
    endSeconds: number;
    // Die Datei, an der die Zeiten genommen wurden (playbackRevisionId der Ansicht).
    expectedPlaybackRevisionId: string;
  },
): Promise<Zeitmarke> {
  const response = await postAuth(basis(recordingId), body);
  if (!response.ok) throw response;
  return ((await response.json()) as { passage: Zeitmarke }).passage;
}

export async function patchZeitmarke(
  recordingId: string,
  passageId: string,
  body: {
    startSeconds: number;
    endSeconds: number;
    expectedVersion: number;
    expectedPlaybackRevisionId: string;
  },
): Promise<Zeitmarke> {
  const response = await patchAuth(
    `${basis(recordingId)}/${encodeURIComponent(passageId)}`,
    body,
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { passage: Zeitmarke }).passage;
}

export async function deleteZeitmarke(
  recordingId: string,
  passageId: string,
  expectedVersion: number,
): Promise<void> {
  const response = await postAuth(
    `${basis(recordingId)}/${encodeURIComponent(passageId)}/delete`,
    { expectedVersion },
  );
  if (!response.ok) throw response;
}

/**
 * Bestätigt genau die genannten Zeitmarken (mit dem Stand, den die Ansicht
 * sah) gegen die Datei, die Mitglieder jetzt sehen.
 */
export async function bestaetigeZeitmarken(
  recordingId: string,
  expectedPlaybackRevisionId: string,
  passages: { id: string; expectedVersion: number }[],
): Promise<{ passages: Zeitmarke[] }> {
  const response = await postAuth(`${basis(recordingId)}/review`, {
    expectedPlaybackRevisionId,
    passages,
  });
  if (!response.ok) throw response;
  return (await response.json()) as { passages: Zeitmarke[] };
}

/**
 * Zeit für Eingabefelder und Anzeige: m:ss oder h:mm:ss, Bruchteile nach dem
 * Komma nur, wenn es welche gibt (auf Tausendstel gerundet).
 */
export function zeitFormat(sekunden: number): string {
  const gerundet = Math.max(0, Math.round(sekunden * 1000) / 1000);
  const ganz = Math.floor(gerundet);
  const bruchteil = Math.round((gerundet - ganz) * 1000);
  const stunden = Math.floor(ganz / 3600);
  const minuten = Math.floor((ganz % 3600) / 60);
  const rest = ganz % 60;
  const mm = stunden > 0 ? String(minuten).padStart(2, "0") : String(minuten);
  const ss = String(rest).padStart(2, "0");
  const text = stunden > 0 ? `${stunden}:${mm}:${ss}` : `${mm}:${ss}`;
  if (bruchteil === 0) return text;
  return `${text},${String(bruchteil).padStart(3, "0").replace(/0+$/, "")}`;
}

/**
 * Liest m:ss, h:mm:ss oder reine Sekunden, Bruchteile mit Komma oder Punkt.
 * null, wenn der Text keine Zeit ist.
 */
export function zeitLesen(text: string): number | null {
  const roh = text.trim().replace(",", ".");
  if (!/^\d+(:\d{1,2}){0,2}(\.\d{1,3})?$/.test(roh)) return null;
  const [ganz, bruch] = roh.split(".");
  const teile = ganz.split(":").map(Number);
  if (teile.slice(1).some((teil) => teil >= 60)) return null;
  const sekunden = teile.reduce((summe, teil) => summe * 60 + teil, 0);
  const wert = sekunden + (bruch ? Number(`0.${bruch}`) : 0);
  return Number.isFinite(wert) ? wert : null;
}

export function bereichText(von: number, bis: number): string {
  return `${zeitFormat(von)} – ${zeitFormat(bis)}`;
}

/** Link in eine Aufnahme, am Anfang einer markierten Stelle. */
export function stellenPfad(
  eventId: string,
  recordingId: string,
  passageId: string,
): string {
  return `/auftritt/?id=${encodeURIComponent(eventId)}&aufnahme=${encodeURIComponent(recordingId)}&stelle=${encodeURIComponent(passageId)}`;
}

/** Link in die ganze Aufnahme, ohne Stelle. */
export function aufnahmePfad(eventId: string, recordingId: string): string {
  return `/auftritt/?id=${encodeURIComponent(eventId)}&aufnahme=${encodeURIComponent(recordingId)}`;
}
