import { authedFetch } from "@/lib/auth";
import type { LesVorschlag } from "@/lib/lied-entwurf/reducer";
import { AbbruchFehler, problemTitel } from "@/lib/uebertragung";

// Das Lesen einer PDF für einen Entwurf (#177). Der Server speichert nichts:
// er antwortet mit Vorschlägen und dem Entwurf und der Anfrage, die wir
// mitgeschickt haben (POST /api/lied-lesungen). Nennt die Anfrage zusätzlich
// die Datei eines gespeicherten Entwurfs (#178), behält der Server das
// Ergebnis für "Angefangen"; die Formen bleiben dieselben.

export class LesenFehler extends Error {
  /** false: die KI steht nicht zur Verfügung (Budget, nicht eingerichtet). */
  readonly verfuegbar: boolean;

  constructor(meldung: string, verfuegbar: boolean) {
    super(meldung);
    this.verfuegbar = verfuegbar;
  }
}

export function istPdf(datei: File): boolean {
  return datei.type === "application/pdf" || /\.pdf$/i.test(datei.name);
}

export async function leseEntwurfPdf(
  entwurfId: string,
  anfrageId: number,
  datei: File,
  signal: AbortSignal,
  /** Die Datei im gespeicherten Entwurf (#178): dann hält der Server das Ergebnis auch fest. */
  dateiId?: string,
): Promise<LesVorschlag[]> {
  const form = new FormData();
  form.set("entwurfId", entwurfId);
  form.set("anfrageId", String(anfrageId));
  if (dateiId) form.set("dateiId", dateiId);
  form.set("datei", datei, datei.name);
  let antwort: Response;
  try {
    antwort = await authedFetch("/api/lied-lesungen", {
      method: "POST",
      body: form,
      signal,
    });
  } catch (ursache) {
    if (signal.aborted) throw new AbbruchFehler();
    throw ursache;
  }
  if (!antwort.ok) {
    const meldung = await problemTitel(
      antwort,
      "Das Lesen hat nicht geklappt. Bitte erneut versuchen.",
    );
    // 409: nicht eingerichtet, 429: Monatsbudget erreicht.
    throw new LesenFehler(meldung, ![409, 429].includes(antwort.status));
  }
  const inhalt = (await antwort.json()) as {
    entwurfId: string;
    anfrageId: number;
    felder: LesVorschlag[];
  };
  // Eine Antwort, die nicht zu dieser Anfrage gehört, wird nicht gelesen.
  if (inhalt.entwurfId !== entwurfId || inhalt.anfrageId !== anfrageId) {
    throw new LesenFehler("Die Antwort gehört zu einer anderen Anfrage.", true);
  }
  return inhalt.felder;
}
