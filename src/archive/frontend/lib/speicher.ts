// #148: Cold Storage der Erhaltungsoriginale. Die Redaktion verschiebt das
// Original ins Cold Storage, sobald Mitglieder es über eine eigene, geprüfte
// Abspielkopie hören bzw. sehen; ein Objekt, das beide Rollen erfüllt, bleibt
// online. Die Helfer folgen der Rezeptur lib/videos.ts: Abrufe same-origin
// ohne Zwischenspeicherung, Fehler als geworfene Antwort, deutsche Texte.

import { postAuth } from "./auth.ts";

/** Die Ziel-Stufe der Verschiebung (Vertrag AssetStorageTiers). */
export type SpeicherTierZiel = "hot" | "cold";

/** Antwort der Verschiebung (Vertrag DateiPayloads.Summary). */
export type SpeicherTierAntwort = {
  datei: {
    id: string;
    storageTier: string;
    tierChangedAt: string | null;
  };
};

/**
 * Verschiebt die Datei auf die gewünschte Online-Stufe; die Antwort trägt den
 * neuen Stand dieser einen Datei. Die rowVersion ist der Stand, aus dem die
 * Redaktion den Knopf gebaut hat — ein zwischenzeitlich geänderter Eintrag
 * antwortet mit 409 statt dem Stillen Überschreiben. Ein Fehlschlag wird
 * geworfen und ändert nichts am Stand.
 */
export async function verschiebeSpeicherTier(
  dateiId: string,
  tier: SpeicherTierZiel,
  rowVersion?: number,
  signal?: AbortSignal,
): Promise<SpeicherTierAntwort> {
  const response = await postAuth(
    `/api/dateien/${encodeURIComponent(dateiId)}/storage-tier`,
    rowVersion === undefined ? { tier } : { tier, rowVersion },
    signal,
  );
  if (!response.ok) throw response;
  return (await response.json()) as SpeicherTierAntwort;
}

/** Das Datum der letzten Stufenänderung als de-AT-Datum; ohne steht keins. */
export function speicherTierDatum(
  tierChangedAt?: string | null,
): string | null {
  if (!tierChangedAt) return null;
  const datum = new Date(tierChangedAt);
  if (Number.isNaN(datum.getTime())) return null;
  return new Intl.DateTimeFormat("de-AT", {
    day: "numeric",
    month: "numeric",
    year: "numeric",
  }).format(datum);
}

/**
 * Deutsche Zustandszeile des Originals; beide Stufen haben ihren ehrlichen
 * Satz, das Datum nennt seit wann die Stufe gilt.
 */
export function speicherTierText(
  storageTier: "hot" | "cold",
  tierChangedAt?: string | null,
): string {
  const seit = speicherTierDatum(tierChangedAt);
  const stufe = storageTier === "cold" ? "Cold Storage" : "Hot Storage";
  return seit
    ? `Original liegt seit ${seit} im ${stufe}.`
    : `Original liegt im ${stufe}.`;
}
