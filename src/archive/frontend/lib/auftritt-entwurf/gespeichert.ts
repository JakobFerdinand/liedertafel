import {
  type DateiArt,
  type DateiEintrag,
  type Feld,
  type KiZustand,
  type LesenZustand,
  type ProgrammZeile,
  type Sicherheit,
  textFelder,
  type Werte,
  type Zustand,
} from "./reducer.ts";

// Der gespeicherte Entwurf (#131): der Teil des Zustands, den das Backend für
// "Angefangen" festhält, und der Weg zurück. Er ist kein zweites Modell.
//
// Gespeichert werden Felder, Bearbeitungsmarken (nur Entwurfszustand, nie am
// Auftritt), die Vorschlagsmarken, das Programm mit den gewählten Liedern und
// die Dateien ohne ihre Bytes. Eine Herkunft der Werte gibt es nicht.

export const FORM_VERSION = 1;

export const UNTERBROCHEN = "Das Lesen wurde unterbrochen. Bitte erneut lesen.";

export type GespeichertesLesen = {
  status: "keine" | "liest" | "gelesen" | "fehlgeschlagen" | "nicht-verfuegbar";
  anfrageId: number;
  meldung: string;
};

export type GespeicherteDatei = {
  id: string;
  name: string;
  sizeBytes: number;
  art: DateiArt;
  lesen: GespeichertesLesen;
  hochgeladen: boolean;
};

export type Gespeichert = {
  version: typeof FORM_VERSION;
  werte: Werte;
  bearbeitet: Feld[];
  vorschlaege: Partial<Record<Feld, Sicherheit>>;
  programm: ProgrammZeile[];
  dateien: GespeicherteDatei[];
  auftrittId: string | null;
  programmGespeichert: boolean;
};

function gespeichertesLesen(lesen: LesenZustand): GespeichertesLesen {
  switch (lesen.status) {
    case "keine":
      return { status: "keine", anfrageId: 0, meldung: "" };
    case "liest":
    case "gelesen":
      return { status: lesen.status, anfrageId: lesen.anfrageId, meldung: "" };
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      return {
        status: lesen.status,
        anfrageId: lesen.anfrageId,
        meldung: lesen.meldung,
      };
  }
}

/**
 * Was vom Entwurf gespeichert wird; null, solange es nichts zu behalten gibt.
 * Ist der Auftritt schon angelegt, bleiben nur die Dateien im Entwurf, die noch
 * nicht oben sind: der Entwurf endet erst, wenn jede Datei hochgeladen oder von
 * der Redaktion entfernt ist.
 */
export function alsGespeichert(zustand: Zustand): Gespeichert | null {
  if (zustand.phase === "gespeichert") return null;
  const angelegt = zustand.auftrittId !== null;
  const dateien = angelegt
    ? zustand.dateien.filter((d) => d.upload.status !== "hochgeladen")
    : zustand.dateien;
  if (dateien.length === 0) return null;
  return {
    version: FORM_VERSION,
    werte: zustand.werte,
    bearbeitet: (Object.keys(zustand.bearbeitet) as Feld[]).filter(
      (feld) => zustand.bearbeitet[feld],
    ),
    vorschlaege: zustand.vorschlaege,
    programm: zustand.programm,
    dateien: dateien.map((d) => ({
      id: d.id,
      name: d.name,
      sizeBytes: d.sizeBytes,
      art: d.art,
      lesen: gespeichertesLesen(d.lesen),
      hochgeladen: d.upload.status === "hochgeladen",
    })),
    auftrittId: zustand.auftrittId,
    programmGespeichert: zustand.programmGespeichert,
  };
}

const istObjekt = (wert: unknown): wert is Record<string, unknown> =>
  typeof wert === "object" && wert !== null && !Array.isArray(wert);

function lesenWiederhergestellt(lesen: GespeichertesLesen): LesenZustand {
  switch (lesen.status) {
    case "keine":
      return { status: "keine" };
    // Die Antwort kam nicht mehr an: erneut lesen, sobald die Datei wieder da ist.
    case "liest":
      return {
        status: "fehlgeschlagen",
        anfrageId: lesen.anfrageId,
        meldung: UNTERBROCHEN,
      };
    case "gelesen":
      return { status: "gelesen", anfrageId: lesen.anfrageId };
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      return {
        status: lesen.status,
        anfrageId: lesen.anfrageId,
        meldung: lesen.meldung,
      };
  }
}

/**
 * Der Zustand eines gespeicherten Entwurfs; null, wenn er nicht die bekannte
 * Form hat. Die Dateien ohne Bytes gelten als "fehlt", bis sie neu gewählt sind.
 */
export function ausGespeichert(
  roh: unknown,
  entwurfId: string,
): Zustand | null {
  if (!istObjekt(roh) || roh.version !== FORM_VERSION) return null;
  const { werte, bearbeitet, vorschlaege, programm, dateien } = roh;
  if (
    !istObjekt(werte) ||
    !Array.isArray(bearbeitet) ||
    !istObjekt(vorschlaege) ||
    !Array.isArray(programm) ||
    !Array.isArray(dateien)
  )
    return null;
  if (
    !textFelder.every((feld) => typeof werte[feld] === "string") ||
    typeof werte.unsicher !== "boolean"
  )
    return null;

  const wiederhergestellt: DateiEintrag[] = [];
  for (const datei of dateien as GespeicherteDatei[]) {
    if (
      !istObjekt(datei) ||
      typeof datei.id !== "string" ||
      !istObjekt(datei.lesen)
    )
      return null;
    wiederhergestellt.push({
      id: datei.id,
      name: datei.name,
      sizeBytes: datei.sizeBytes,
      art: datei.art,
      inhalt: "fehlt",
      lesen: lesenWiederhergestellt(datei.lesen),
      upload: datei.hochgeladen
        ? { status: "hochgeladen" }
        : { status: "wartend" },
    });
  }
  const ki: KiZustand = wiederhergestellt.some(
    (d) => d.lesen.status === "nicht-verfuegbar",
  )
    ? {
        status: "nicht-verfuegbar",
        meldung: (
          wiederhergestellt.find((d) => d.lesen.status === "nicht-verfuegbar")
            ?.lesen as { meldung: string }
        ).meldung,
      }
    : { status: "verfuegbar" };
  const auftrittId = typeof roh.auftrittId === "string" ? roh.auftrittId : null;
  return {
    entwurfId,
    phase: auftrittId ? "speichern" : "sammeln",
    werte: werte as unknown as Werte,
    bearbeitet: Object.fromEntries(
      (bearbeitet as Feld[]).map((feld) => [feld, true]),
    ),
    vorschlaege: vorschlaege as Partial<Record<Feld, Sicherheit>>,
    programm: programm as ProgrammZeile[],
    dateien: wiederhergestellt,
    ki,
    auftrittId,
    programmGespeichert: roh.programmGespeichert === true,
    fehler: null,
  };
}
