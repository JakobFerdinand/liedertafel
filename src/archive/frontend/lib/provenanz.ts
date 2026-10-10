import { postAuth } from "@/lib/auth";

// ARC-013-1: die Verwaltung der Vorschläge und der Feldherkunft. Alles hier
// läuft über gespeicherte Angaben im Archiv (keine Modellaufrufe), damit die
// Verwaltung auch bei pausierter KI funktioniert.

export type Vorschlag = {
  id: string;
  kind: string;
  kindLabel: string;
  targetEntityType: string | null;
  targetEntityId: string | null;
  payload: unknown;
  reason: string;
  confidence: "Sicher" | "Unsicher";
  source: "Human" | "Regex" | "Ai";
  model: string | null;
  promptVersion: string | null;
  sourceDescription: string | null;
  targetRowVersion: number | null;
  targetCurrentRowVersion: number | null;
  targetCurrentSummary: string | null;
  isStale: boolean;
  createdAt: string;
};

export type Vorschlagsliste = {
  proposals: Vorschlag[];
};

export async function fetchVorschlaege(
  signal?: AbortSignal,
): Promise<Vorschlagsliste> {
  const response = await fetch("/api/proposals", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as Vorschlagsliste;
}

/** Nimmt einen Vorschlag an; ein veraltetes Ziel antwortet 409 (mit Zusatz). */
export async function annehmenVorschlag(id: string): Promise<Response> {
  return postAuth(`/api/proposals/${encodeURIComponent(id)}/accept`, {});
}

/** Lehnt einen Vorschlag ab. */
export async function ablehnenVorschlag(id: string): Promise<Response> {
  return postAuth(`/api/proposals/${encodeURIComponent(id)}/reject`, {});
}

/** Bezieht einen als veraltet erkannten Vorschlag auf den neuen Stand. */
export async function auffrischenVorschlag(id: string): Promise<Response> {
  return postAuth(`/api/proposals/${encodeURIComponent(id)}/refresh`, {});
}

/** Holt den früheren Wert eines Feldes zurück (sichert das Feld). */
export async function zurueckholenFeld(
  entityType: string,
  entityId: string,
  field: string,
): Promise<Response> {
  return postAuth("/api/provenance/revert", { entityType, entityId, field });
}

/** Liest den Konfliktzusatz (aktuell/geplant) aus einer 409-Antwort. */
export async function leseKonflikt(antwort: Response): Promise<{
  titel: string;
  aktuellerWert: string | null;
  vorgeschlagenerWert: string | null;
} | null> {
  if (antwort.status !== 409) return null;
  const inhalt = (await antwort.json().catch(() => null)) as {
    title?: unknown;
    currentValue?: unknown;
    proposedValue?: unknown;
  } | null;
  if (!inhalt || typeof inhalt.title !== "string") return null;
  return {
    titel: inhalt.title,
    aktuellerWert:
      typeof inhalt.currentValue === "string"
        ? inhalt.currentValue
        : inhalt.currentValue == null
          ? null
          : String(inhalt.currentValue),
    vorgeschlagenerWert:
      typeof inhalt.proposedValue === "string"
        ? inhalt.proposedValue
        : inhalt.proposedValue == null
          ? null
          : String(inhalt.proposedValue),
  };
}
