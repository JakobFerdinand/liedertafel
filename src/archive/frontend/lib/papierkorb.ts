import { postAuth } from "@/lib/auth";

// #146: Wiederherstellen und endgültiges Entfernen aus dem Papierkorb.
// Wiederherstellen ist Redaktionssache; das endgültige Entfernen darf nur die
// Verwaltung (Administrator).

export type PapierkorbArt = "lieder" | "auftritte" | "dateien";

/** Stellt einen gelöschten Eintrag aus dem Papierkorb wieder her. */
export async function stelleWiederHer(
  art: PapierkorbArt,
  id: string,
): Promise<void> {
  const pfad =
    art === "lieder" ? "lieder" : art === "auftritte" ? "events" : "dateien";
  const response = await postAuth(
    `/api/${pfad}/${encodeURIComponent(id)}/restore`,
    {},
  );
  if (!response.ok) throw response;
}

/** Entfernt einen Papierkorb-Eintrag endgültig (nur Verwaltung). */
export async function entferneEndgueltig(
  art: PapierkorbArt,
  id: string,
): Promise<void> {
  const pfad =
    art === "lieder" ? "lieder" : art === "auftritte" ? "auftritte" : "dateien";
  const response = await postAuth(
    `/api/papierkorb/${pfad}/${encodeURIComponent(id)}/remove`,
    {},
  );
  if (!response.ok) throw response;
}
