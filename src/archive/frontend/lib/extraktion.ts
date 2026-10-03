// ARC-034: Auswertungsstand der PDF-Textauswertung für die Redaktion.
// Die Helfer folgen der Rezeptur lib/assets.ts: Abrufe same-origin ohne
// Zwischenspeicherung, Fehler als geworfene Antwort, deutsche Zustandstexte.

import { postAuth } from "@/lib/auth";

/** Höchstzahl der Revisionen je Anfrage (Vertrag ExtractionEndpoints). */
export const extraktionsLimit = 100;

/** Zustände eines Auswertungslaufs (Vertrag ExtractionEndpoints). */
export type ExtraktionsStatus =
  | "queued"
  | "running"
  | "completed"
  | "noText"
  | "failed";

/** Eine Auswertungszeile des Stapelabrufs (Vertrag StatusPayload). */
export type ExtraktionsInfo = {
  revisionId: string;
  assetId: string;
  revisionNumber: number;
  status: ExtraktionsStatus;
  text: string | null;
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
