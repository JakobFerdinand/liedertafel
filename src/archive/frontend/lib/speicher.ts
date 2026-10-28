// #148: Cold Storage der Erhaltungsoriginale. Die Redaktion verschiebt das
// Original ins Cold Storage, sobald Mitglieder es über eine eigene, geprüfte
// Abspielkopie hören bzw. sehen; ein Objekt, das beide Rollen erfüllt, bleibt
// online. Die Helfer folgen der Rezeptur lib/videos.ts: Abrufe same-origin
// ohne Zwischenspeicherung, Fehler als geworfene Antwort, deutsche Texte.

import { postAuth } from "@/lib/auth";

/** Der Ziel-Tier der Verschiebung (Vertrag AssetStorageTiers). */
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
 * Verschiebt die Datei in das gewünschte Online-Tier; die Antwort trägt den
 * neuen Stand dieser einen Datei. Ein Fehlschlag wird geworfen und ändert
 * nichts am Stand.
 */
export async function verschiebeSpeicherTier(
  dateiId: string,
  tier: SpeicherTierZiel,
  signal?: AbortSignal,
): Promise<SpeicherTierAntwort> {
  const response = await postAuth(
    `/api/dateien/${encodeURIComponent(dateiId)}/storage-tier`,
    { tier },
    signal,
  );
  if (!response.ok) throw response;
  return (await response.json()) as SpeicherTierAntwort;
}

/** Deutsche Zustandszeile; "hot" ist der leise Grundzustand ohne Zeile. */
export function speicherTierText(tier: string): string {
  switch (tier) {
    case "cold":
      return "Original liegt im Cold Storage.";
    default:
      return "";
  }
}
