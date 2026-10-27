import type {
  LesProgrammpunkt,
  LesVorschlag,
} from "@/lib/auftritt-entwurf/reducer";
import { authedFetch } from "@/lib/auth";
import { AbbruchFehler, problemTitel } from "@/lib/uebertragung";

// Das Lesen eines Programmzettels für einen Auftritt (#131). Der Server
// speichert nichts: er antwortet mit Vorschlägen für das Formular, dem
// Programm mit den Treffern im Liederverzeichnis und dem Entwurf und der
// Anfrage, die wir mitgeschickt haben (POST /api/auftritt-lesungen).

export class LesenFehler extends Error {
  /** false: die KI steht nicht zur Verfügung (Budget, nicht eingerichtet). */
  readonly verfuegbar: boolean;

  constructor(meldung: string, verfuegbar: boolean) {
    super(meldung);
    this.verfuegbar = verfuegbar;
  }
}

export type Lesung = { felder: LesVorschlag[]; programm: LesProgrammpunkt[] };

export async function leseProgramm(
  entwurfId: string,
  anfrageId: number,
  datei: File,
  signal: AbortSignal,
): Promise<Lesung> {
  const form = new FormData();
  form.set("entwurfId", entwurfId);
  form.set("anfrageId", String(anfrageId));
  form.set("datei", datei, datei.name);
  let antwort: Response;
  try {
    antwort = await authedFetch("/api/auftritt-lesungen", {
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
    programm: LesProgrammpunkt[];
  };
  // Eine Antwort, die nicht zu dieser Anfrage gehört, wird nicht gelesen.
  if (inhalt.entwurfId !== entwurfId || inhalt.anfrageId !== anfrageId) {
    throw new LesenFehler("Die Antwort gehört zu einer anderen Anfrage.", true);
  }
  return { felder: inhalt.felder, programm: inhalt.programm };
}
