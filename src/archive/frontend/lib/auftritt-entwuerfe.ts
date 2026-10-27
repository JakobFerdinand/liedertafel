import type { Gespeichert } from "@/lib/auftritt-entwurf/gespeichert";
import { authedFetch } from "@/lib/auth";

// "Angefangen" (#131): der Entwurf eines Auftritts, den das Backend für die
// Redaktion festhält. Das Backend ist maßgeblich; der Browser schickt sein
// ganzes Formular (`PUT`, der letzte Stand gilt) und holt es beim Fortsetzen
// von dort zurück. Gespeichert werden Felder, Bearbeitungsmarken, Programm und
// die Dateien ohne Bytes, keine Herkunft von Werten.

export type AuftrittEntwurfEintrag = {
  entwurfId: string;
  titel: string;
  dateien: number;
  aktualisiertAm: string;
};

export class AuftrittEntwurfFehler extends Error {
  readonly nichtGefunden: boolean;

  constructor(meldung: string, nichtGefunden = false) {
    super(meldung);
    this.nichtGefunden = nichtGefunden;
  }
}

const pfad = (id: string) =>
  `/api/auftritt-entwuerfe/${encodeURIComponent(id)}`;

/** Die angefangenen Einträge; null, wenn die Redaktion sie nicht sehen darf. */
export async function ladeAuftrittEntwuerfe(
  signal?: AbortSignal,
): Promise<AuftrittEntwurfEintrag[] | null> {
  const antwort = await fetch("/api/auftritt-entwuerfe", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (antwort.status === 401 || antwort.status === 403) return null;
  if (!antwort.ok)
    throw new AuftrittEntwurfFehler(
      "Angefangenes konnte nicht geladen werden.",
    );
  return ((await antwort.json()) as { entwuerfe: AuftrittEntwurfEintrag[] })
    .entwuerfe;
}

/** Das gespeicherte Formular roh; `ausGespeichert` prüft seine Form. */
export async function ladeAuftrittEntwurf(
  id: string,
  signal?: AbortSignal,
): Promise<unknown> {
  const antwort = await fetch(pfad(id), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (antwort.status === 404) {
    throw new AuftrittEntwurfFehler(
      "Der angefangene Eintrag wurde nicht gefunden.",
      true,
    );
  }
  if (!antwort.ok)
    throw new AuftrittEntwurfFehler("Der Eintrag konnte nicht geladen werden.");
  return ((await antwort.json()) as { entwurf: { formular: unknown } }).entwurf
    .formular;
}

/** `schliessend`: die Seite wird verlassen, die Anfrage soll es überleben. */
export async function speichereAuftrittEntwurf(
  entwurfId: string,
  titel: string,
  formular: Gespeichert,
  schliessend = false,
): Promise<void> {
  const antwort = await authedFetch(pfad(entwurfId), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      entwurfId,
      titel: titel.trim().slice(0, 200),
      dateien: formular.dateien.length,
      formular,
    }),
    keepalive: schliessend,
  });
  if (!antwort.ok)
    throw new AuftrittEntwurfFehler(
      "Der Entwurf konnte nicht gespeichert werden.",
    );
}

export async function verwirfAuftrittEntwurf(id: string): Promise<void> {
  const antwort = await authedFetch(pfad(id), { method: "DELETE" });
  if (!antwort.ok)
    throw new AuftrittEntwurfFehler(
      "Der Entwurf konnte nicht verworfen werden.",
    );
}
