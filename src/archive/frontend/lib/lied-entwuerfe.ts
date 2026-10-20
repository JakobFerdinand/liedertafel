import { authedFetch } from "@/lib/auth";
import type { Gespeichert } from "@/lib/lied-entwurf/gespeichert";

// "Angefangen" (#178): der Entwurf, den das Backend für die Redaktion festhält.
// Das Backend ist maßgeblich; der Browser schickt sein ganzes Formular
// (`PUT`, der letzte Stand gilt) und holt es beim Fortsetzen von dort zurück.
// Gespeichert werden Felder, Bearbeitungsmarken und die Dateien ohne Bytes,
// keine Herkunft von Werten.

export type EntwurfEintrag = {
  entwurfId: string;
  titel: string;
  dateien: number;
  aktualisiertAm: string;
};

export class EntwurfFehler extends Error {
  readonly nichtGefunden: boolean;

  constructor(meldung: string, nichtGefunden = false) {
    super(meldung);
    this.nichtGefunden = nichtGefunden;
  }
}

const pfad = (id: string) => `/api/lied-entwuerfe/${encodeURIComponent(id)}`;

/** Die angefangenen Einträge; null, wenn die Redaktion sie nicht sehen darf. */
export async function ladeEntwuerfe(
  signal?: AbortSignal,
): Promise<EntwurfEintrag[] | null> {
  const antwort = await fetch("/api/lied-entwuerfe", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (antwort.status === 401 || antwort.status === 403) return null;
  if (!antwort.ok)
    throw new EntwurfFehler("Angefangenes konnte nicht geladen werden.");
  const inhalt = (await antwort.json()) as { entwuerfe: EntwurfEintrag[] };
  return inhalt.entwuerfe;
}

export async function ladeEntwurf(
  id: string,
  signal?: AbortSignal,
): Promise<Gespeichert> {
  const antwort = await fetch(pfad(id), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (antwort.status === 404) {
    throw new EntwurfFehler(
      "Der angefangene Eintrag wurde nicht gefunden.",
      true,
    );
  }
  if (!antwort.ok)
    throw new EntwurfFehler("Der Eintrag konnte nicht geladen werden.");
  const inhalt = (await antwort.json()) as { entwurf: Gespeichert };
  return inhalt.entwurf;
}

/** Der Server führt die Lesungen selbst; sie gehören nicht in den Abzug. */
function ohneLesungen(entwurf: Gespeichert) {
  return {
    ...entwurf,
    dateien: entwurf.dateien.map(({ lesung: _lesung, ...datei }) => datei),
  };
}

/** `schliessend`: die Seite wird verlassen, die Anfrage soll es überleben. */
export async function speichereEntwurf(
  entwurf: Gespeichert,
  schliessend = false,
): Promise<void> {
  const antwort = await authedFetch(pfad(entwurf.entwurfId), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(ohneLesungen(entwurf)),
    keepalive: schliessend,
  });
  if (!antwort.ok)
    throw new EntwurfFehler("Der Entwurf konnte nicht gespeichert werden.");
}

export async function verwirfEntwurf(id: string): Promise<void> {
  const antwort = await authedFetch(pfad(id), { method: "DELETE" });
  if (!antwort.ok)
    throw new EntwurfFehler("Der Entwurf konnte nicht verworfen werden.");
}
