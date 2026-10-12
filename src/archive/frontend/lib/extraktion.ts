// ARC-034: Auswertungsstand der PDF-Textauswertung für die Redaktion.
// Die Helfer folgen der Rezeptur lib/assets.ts: Abrufe same-origin ohne
// Zwischenspeicherung, Fehler als geworfene Antwort, deutsche Zustandstexte.

import { postAuth } from "@/lib/auth";
import type { NotenAngaben } from "@/lib/songs";

/** Höchstzahl der Revisionen je Anfrage (Vertrag ExtractionEndpoints). */
export const extraktionsLimit = 100;

/** Zustände eines Auswertungslaufs (Vertrag ExtractionEndpoints). */
export type ExtraktionsStatus =
  | "queued"
  | "running"
  | "completed"
  | "noText"
  | "failed";

/** Zustände des KI-Lesens der Seiten (Vertrag ReadingEndpoints.ReadingPayload). */
export type LeseStatus =
  | "queued"
  | "running"
  | "waitingForBudget"
  | "completed"
  | "failed";

/** ARC-034-1: Stand des KI-Lesens einer Notendatei. */
export type LeseInfo = {
  status: LeseStatus;
  pagesTotal: number | null;
  pagesRead: number;
  /** „teilweise gelesen“: die Datei hat mehr Seiten als bisher gelesen wurden. */
  partial: boolean;
  fieldsApplied: number;
  fieldsProposed: number;
  failureReason: string | null;
  attemptCount: number;
  completedAt: string | null;
  updatedAt: string | null;
  rowVersion: number;
};

/** Eine Auswertungszeile des Stapelabrufs (Vertrag StatusPayload). */
export type ExtraktionsInfo = {
  revisionId: string;
  assetId: string;
  revisionNumber: number;
  status: ExtraktionsStatus;
  /** Woher der Text stammt: eingebettet oder von der KI aus dem Scan gelesen. */
  textSource?: "embedded" | "reading";
  /** KI-Lesen der Seiten; null, solange es keine Zeile dafür gibt. */
  reading?: LeseInfo | null;
  text: string | null;
  /** Lesbarer Text ohne Notenzeichen, Silben zu Wörtern verbunden. */
  cleanText?: string | null;
  /** Aus dem Text erkannte Angaben; null, wenn nichts erkannt wurde. */
  facts?: NotenAngaben | null;
  failureReason: string | null;
  attemptCount: number;
  completedAt: string | null;
  lastAttemptAt: string | null;
  lastEnqueuedAt: string | null;
  updatedAt: string | null;
  rowVersion: number;
};

/**
 * Auswertungsstand mehrerer Revisionen, in der Reihenfolge der Anfrage;
 * unbekannte Kennungen fehlen in der Antwort still. Ohne Kennungen
 * entsteht keine Anfrage. Größere Listen werden nacheinander abgefragt.
 */
export async function holeExtraktionStatus(
  revisionIds: string[],
  signal?: AbortSignal,
): Promise<ExtraktionsInfo[]> {
  const results: ExtraktionsInfo[] = [];
  for (let start = 0; start < revisionIds.length; start += extraktionsLimit) {
    const parameter = new URLSearchParams({
      ids: revisionIds.slice(start, start + extraktionsLimit).join(","),
    });
    const response = await fetch(`/api/revisions/extraction?${parameter}`, {
      credentials: "same-origin",
      cache: "no-store",
      signal,
    });
    if (!response.ok) throw response;
    const data = (await response.json()) as { results?: ExtraktionsInfo[] };
    results.push(...(data.results ?? []));
  }
  return results;
}

/**
 * Startet die Auswertung einer Revision erneut (oder legt die fehlende
 * Zeile an); die Antwort trägt den aktuellen Stand dieser einen Revision.
 */
export async function starteExtraktionErneut(
  revisionId: string,
  signal?: AbortSignal,
): Promise<ExtraktionsInfo> {
  const response = await postAuth(
    `/api/revisions/${encodeURIComponent(revisionId)}/extraction/retry`,
    {},
    signal,
  );
  if (!response.ok) throw response;
  return (await response.json()) as ExtraktionsInfo;
}

async function lesenAnstossen(
  revisionId: string,
  aktion: "retry" | "continue",
  signal?: AbortSignal,
): Promise<LeseInfo> {
  const response = await postAuth(
    `/api/revisions/${encodeURIComponent(revisionId)}/reading/${aktion}`,
    {},
    signal,
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { reading: LeseInfo }).reading;
}

/** Startet das KI-Lesen erneut (oder legt die fehlende Zeile an). */
export function starteLesenErneut(revisionId: string, signal?: AbortSignal) {
  return lesenAnstossen(revisionId, "retry", signal);
}

/** „Rest lesen“: die nächsten Seiten einer teilweise gelesenen Datei. */
export function leseRest(revisionId: string, signal?: AbortSignal) {
  return lesenAnstossen(revisionId, "continue", signal);
}

/** Läuft das KI-Lesen noch (die Liste fragt dann weiter nach)? */
export function leseLaeuft(info?: LeseInfo | null): boolean {
  return info?.status === "queued" || info?.status === "running";
}

function mehrzahl(anzahl: number, einzahl: string, mehr: string): string {
  return `${anzahl} ${anzahl === 1 ? einzahl : mehr}`;
}

/** Deutsche Zustandszeile des KI-Lesens; null, wenn es nichts zu sagen gibt. */
export function leseStatusText(info: LeseInfo): string {
  const seiten =
    info.pagesTotal !== null
      ? ` (${info.pagesRead} von ${info.pagesTotal} Seiten)`
      : "";
  switch (info.status) {
    case "queued":
      return "KI-Lesen wartet …";
    case "running":
      return `KI liest die Seiten …${seiten}`;
    case "waitingForBudget":
      return "Das KI-Monatsbudget ist erreicht. Das Lesen wartet bis zum nächsten Monat oder bis das Budget erhöht wird. Die Angaben können weiter von Hand eingetragen werden.";
    case "failed":
      return "KI-Lesen gescheitert";
    case "completed": {
      const teile = [
        info.partial
          ? `Teilweise gelesen${seiten}`
          : info.pagesTotal !== null
            ? `Alle ${info.pagesTotal} ${info.pagesTotal === 1 ? "Seite" : "Seiten"} gelesen`
            : "Angaben aus dem Text geprüft",
        mehrzahl(info.fieldsApplied, "Angabe übernommen", "Angaben übernommen"),
        mehrzahl(info.fieldsProposed, "Vorschlag", "Vorschläge"),
      ];
      return teile.join(" · ");
    }
  }
}

/** Deutsche Zustandszeile für die Oberfläche. */
export function extraktionsStatusText(status: ExtraktionsStatus): string {
  switch (status) {
    case "queued":
      return "Textauswertung wartet …";
    case "running":
      return "Textauswertung läuft …";
    case "completed":
      return "Text ausgelesen";
    case "noText":
      return "Kein Text gefunden — vermutlich eingescannt.";
    case "failed":
      return "Textauswertung gescheitert";
  }
}
