/**
 * Der KI-Kostenüberblick (ARC-043, #150): lesende Sicht der Redaktion auf
 * das monatliche KI-Budget des Archivs. Der Backend-Endpunkt liest nur das
 * Verbrauchsbuch; hier wird nichts ausgelöst und nichts gerundet.
 */

export type KiKostenEintrag = {
  feature: string;
  model: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  spentEur: number;
};

export type KiKostenUeberblick = {
  month: string;
  capEur: number;
  thresholdsEur: number[];
  spentEur: number;
  notifiedEur: number;
  features: KiKostenEintrag[];
};

export async function fetchKiKosten(
  signal?: AbortSignal,
): Promise<KiKostenUeberblick> {
  const response = await fetch("/api/maintenance/ai-budget", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as KiKostenUeberblick;
}
