import {
  type DateiEntwurf,
  type DateiTyp,
  type Ereignis,
  type LesenZustand,
  type LesVorschlag,
  type LiedFeld,
  liedFelder,
  type Sicherheit,
  type Zustand,
} from "./reducer.ts";

// Der gespeicherte Entwurf (#178): der Teil des Zustands, den das Backend für
// "Angefangen" festhält, und der Weg zurück. Er ist kein zweites Modell. Das
// Wiederherstellen liefert den Anfangszustand desselben Reducers und die
// Ereignisse, mit denen Lesungen ankommen, die während der Abwesenheit fertig
// wurden; Auftrag, Vorrang der Handarbeit und Veraltetes entscheidet weiter
// allein der Reducer.
//
// Gespeichert werden Felder, Bearbeitungsmarken (nur Entwurfszustand, nie am
// Lied oder an der Datei) und die Dateien ohne ihre Bytes. Eine Herkunft der
// Werte gibt es nicht.

export type LesenStatus =
  | "keine"
  | "liest"
  | "gelesen"
  | "fehlgeschlagen"
  | "nicht-verfuegbar";

/** Der Stand des Lesens, wie der Browser ihn zuletzt gespeichert hat. */
export type GespeichertesLesen = {
  status: LesenStatus;
  anfrageId: number;
  meldung: string;
};

/** Das Ergebnis des Lesens, das der Server führt (Antwortform von #177). */
export type GespeicherteLesung = {
  anfrageId: number;
  status: Exclude<LesenStatus, "keine">;
  meldung: string;
  felder: LesVorschlag[];
};

export type GespeicherteDatei = {
  id: string;
  name: string;
  sizeBytes: number;
  art: DateiTyp;
  stimme: string;
  artBearbeitet: boolean;
  stimmeBearbeitet: boolean;
  lesen: GespeichertesLesen;
  /** Nur beim Laden vom Server; wird nicht mitgeschickt. */
  lesung: GespeicherteLesung | null;
};

export type Gespeichert = {
  entwurfId: string;
  lied: Record<LiedFeld, string>;
  bearbeitet: LiedFeld[];
  vorschlaege: Partial<Record<LiedFeld, Sicherheit>>;
  dateien: GespeicherteDatei[];
};

export const UNTERBROCHEN = "Das Lesen wurde unterbrochen. Bitte erneut lesen.";

export function gespeichertesLesen(lesen: LesenZustand): GespeichertesLesen {
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

function lesenWiederhergestellt(lesen: GespeichertesLesen): LesenZustand {
  switch (lesen.status) {
    case "keine":
      return { status: "keine" };
    case "liest":
    case "gelesen":
      return { status: lesen.status, anfrageId: lesen.anfrageId };
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      return {
        status: lesen.status,
        anfrageId: lesen.anfrageId,
        meldung: lesen.meldung,
      };
  }
}

/** Was vom Entwurf gespeichert wird; null, solange es nichts zu behalten gibt. */
export function alsGespeichert(zustand: Zustand): Gespeichert | null {
  if (zustand.phase !== "sammeln") return null;
  return {
    entwurfId: zustand.entwurfId,
    lied: zustand.lied,
    bearbeitet: liedFelder.filter((feld) => zustand.bearbeitet[feld]),
    vorschlaege: zustand.vorschlaege,
    dateien: zustand.dateien.map((d) => ({
      id: d.id,
      name: d.name,
      sizeBytes: d.sizeBytes,
      art: d.art,
      stimme: d.stimme,
      artBearbeitet: d.bearbeitet.art,
      stimmeBearbeitet: d.bearbeitet.stimme,
      lesen: gespeichertesLesen(d.lesen),
      lesung: null,
    })),
  };
}

export type Wiederhergestellt = {
  zustand: Zustand;
  /** Ergebnisse, die der Server schon hat und der Browser noch nicht kennt. */
  ereignisse: Ereignis[];
  /** Dateien, deren Lesen auf dem Server noch läuft: später erneut fragen. */
  laeuft: string[];
};

/** Die Ereignisse, mit denen das Ergebnis des Servers beim Browser ankommt. */
export function lesungsEreignisse(
  entwurfId: string,
  datei: GespeicherteDatei,
): { ereignisse: Ereignis[]; laeuft: boolean } {
  const { lesen, lesung } = datei;
  const dateiId = datei.id;
  const unterbrochen = (anfrageId: number): Ereignis => ({
    type: "lesen-fehlgeschlagen",
    entwurfId,
    dateiId,
    anfrageId,
    meldung: UNTERBROCHEN,
    verfuegbar: true,
  });

  if (lesung === null || lesung.anfrageId < lesen.anfrageId) {
    return {
      ereignisse:
        lesen.status === "liest" ? [unterbrochen(lesen.anfrageId)] : [],
      laeuft: false,
    };
  }
  const bekannt =
    lesung.anfrageId === lesen.anfrageId && lesen.status !== "liest";
  if (bekannt) return { ereignisse: [], laeuft: false };

  const ereignisse: Ereignis[] = [];
  if (lesung.anfrageId > lesen.anfrageId) {
    ereignisse.push({
      type: "lesen-angefordert",
      entwurfId,
      dateiId,
      anfrageId: lesung.anfrageId,
    });
  }
  switch (lesung.status) {
    case "liest":
      return { ereignisse, laeuft: true };
    case "gelesen":
      ereignisse.push({
        type: "lesen-ergebnis",
        entwurfId,
        dateiId,
        anfrageId: lesung.anfrageId,
        felder: lesung.felder,
      });
      break;
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      ereignisse.push({
        type: "lesen-fehlgeschlagen",
        entwurfId,
        dateiId,
        anfrageId: lesung.anfrageId,
        meldung: lesung.meldung,
        verfuegbar: lesung.status === "fehlgeschlagen",
      });
      break;
  }
  return { ereignisse, laeuft: false };
}

/** Der gespeicherte Entwurf als Anfangszustand des Reducers. */
export function wiederhergestellt(gespeichert: Gespeichert): Wiederhergestellt {
  const bearbeitet = Object.fromEntries(
    liedFelder.map((feld) => [feld, gespeichert.bearbeitet.includes(feld)]),
  ) as Record<LiedFeld, boolean>;
  const dateien: DateiEntwurf[] = gespeichert.dateien.map((d) => ({
    id: d.id,
    name: d.name,
    sizeBytes: d.sizeBytes,
    art: d.art,
    stimme: d.stimme,
    bearbeitet: { art: d.artBearbeitet, stimme: d.stimmeBearbeitet },
    inhalt: "fehlt",
    upload: { status: "wartend" },
    lesen: lesenWiederhergestellt(d.lesen),
  }));
  const ereignisse: Ereignis[] = [];
  const laeuft: string[] = [];
  for (const datei of gespeichert.dateien) {
    const aus = lesungsEreignisse(gespeichert.entwurfId, datei);
    ereignisse.push(...aus.ereignisse);
    if (aus.laeuft) laeuft.push(datei.id);
  }
  return {
    zustand: {
      phase: "sammeln",
      entwurfId: gespeichert.entwurfId,
      lied: gespeichert.lied,
      bearbeitet,
      vorschlaege: gespeichert.vorschlaege,
      dateien,
      fehler: null,
    },
    ereignisse,
    laeuft,
  };
}
