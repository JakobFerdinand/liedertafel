// #161: Abspielkopien der Video-Aufnahmen. Die Redaktion sieht den Stand der
// automatischen MP4-Konvertierung am Eintrag und kann fehlgeschlagene Läufe
// erneut starten. Die Helfer folgen der Rezeptur lib/extraktion.ts: Abrufe
// same-origin ohne Zwischenspeicherung, Fehler als geworfene Antwort,
// deutsche Zustandstexte.

import { postAuth } from "@/lib/auth";

/** Ist die Datei Video-Aufnahmematerial (Vertrag VideoService)? */
export function istVideoMaterial(kind: string, contentType: string): boolean {
  return kind === "aufnahme" && contentType.toLowerCase().startsWith("video/");
}
/** Höchstzahl der Dateien je Anfrage (Vertrag VideoEndpoints). */
export const videoStatusLimit = 100;

/** Zustände eines Konvertierungslaufs (Vertrag StatusPayload). */
export type VideoStatus =
  | "queued"
  | "running"
  | "completed"
  | "notNeeded"
  | "failed";

/** Eine Konvertierungszeile des Stapelabrufs (Vertrag StatusPayload). */
export type VideoInfo = {
  dateiId: string;
  status: VideoStatus;
  playbackDateiId: string | null;
  failureReason: string | null;
  attemptCount: number;
  completedAt: string | null;
  lastAttemptAt: string | null;
  lastEnqueuedAt: string | null;
  updatedAt: string | null;
  rowVersion: number;
};

/**
 * Konvertierungsstand mehrerer Dateien, in der Reihenfolge der Anfrage;
 * unbekannte Kennungen fehlen in der Antwort still. Größere Listen werden
 * nacheinander abgefragt.
 */
export async function holeVideoStatus(
  dateiIds: string[],
  signal?: AbortSignal,
): Promise<VideoInfo[]> {
  const results: VideoInfo[] = [];
  for (let start = 0; start < dateiIds.length; start += videoStatusLimit) {
    const parameter = new URLSearchParams({
      ids: dateiIds.slice(start, start + videoStatusLimit).join(","),
    });
    const response = await fetch(`/api/dateien/videos?${parameter}`, {
      credentials: "same-origin",
      cache: "no-store",
      signal,
    });
    if (!response.ok) throw response;
    const data = (await response.json()) as { results?: VideoInfo[] };
    results.push(...(data.results ?? []));
  }
  return results;
}

/**
 * Startet die Konvertierung einer Datei erneut (oder legt die fehlende Zeile
 * an); die Antwort trägt den aktuellen Stand dieser einen Datei.
 */
export async function starteKonvertierungErneut(
  dateiId: string,
  signal?: AbortSignal,
): Promise<VideoInfo> {
  const response = await postAuth(
    `/api/dateien/${encodeURIComponent(dateiId)}/video/retry`,
    {},
    signal,
  );
  if (!response.ok) throw response;
  return (await response.json()) as VideoInfo;
}

/** Läuft die Konvertierung noch (die Liste fragt dann weiter nach)? */
export function konvertierungLaeuft(info?: VideoInfo | null): boolean {
  return info?.status === "queued" || info?.status === "running";
}

/** Deutsche Zustandszeile für die Oberfläche. */
export function videoStatusText(info: VideoInfo): string {
  switch (info.status) {
    case "queued":
      return "Abspielkopie wartet …";
    case "running":
      return "Abspielkopie wird erstellt …";
    case "completed":
      return "Abspielkopie erstellt";
    case "notNeeded":
      return "Das Original ist im Browser abspielbar; keine Abspielkopie nötig.";
    case "failed":
      return "Abspielkopie gescheitert";
  }
}
