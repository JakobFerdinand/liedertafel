// Der Entwurf eines Auftritts aus abgelegten Dateien (#131), nach dem Muster
// des Lied-Entwurfs (#159).
//
// Die Schnittstelle sind Zustand, Ereignis und `reduziere`: eine reine
// Funktion ohne Netz, Zeit oder React. Lesen, Hochladen und Anlegen laufen im
// Hook (`use-auftritt-entwurf.ts`) und melden sich mit Ereignissen zurück. Was
// zu einer überholten Anfrage oder einer entfernten Datei gehört, ändert
// nichts.
//
// Welche Felder die Redaktion angefasst hat, gehört nur zum Entwurf. Es wird
// nie am Auftritt gespeichert und nie als Herkunft angezeigt (ADR-0001).

export const textFelder = [
  "title",
  "kind",
  "jahr",
  "monat",
  "tag",
  "venue",
] as const;

export type TextFeld = (typeof textFelder)[number];

/** Alle Felder, die ein Lesen vorschlagen kann: die Textfelder und "Datum unsicher". */
export type Feld = TextFeld | "unsicher";

export type Sicherheit = "sicher" | "unsicher";

export type Werte = Record<TextFeld, string> & { unsicher: boolean };

/** Programm: Scan oder Foto, das gelesen wird. Aufnahme: Video oder Ton. Sonstiges: weiteres Dokument. */
export type DateiArt = "programm" | "aufnahme" | "sonstiges";

export type LesenZustand =
  | { status: "keine" }
  | { status: "liest"; anfrageId: number }
  | { status: "gelesen"; anfrageId: number }
  | { status: "fehlgeschlagen"; anfrageId: number; meldung: string }
  | { status: "nicht-verfuegbar"; anfrageId: number; meldung: string };

export type UploadZustand =
  | { status: "wartend" }
  | { status: "laedt"; bytes: number; gesamt: number }
  | { status: "hochgeladen" }
  | { status: "fehlgeschlagen"; meldung: string };

export type DateiEintrag = {
  id: string;
  name: string;
  sizeBytes: number;
  art: DateiArt;
  /** Die Bytes liegen im Browser; nach dem Wiederherstellen fehlen sie, bis die Datei erneut gewählt wird. */
  inhalt: "vorhanden" | "fehlt";
  lesen: LesenZustand;
  upload: UploadZustand;
};

export type LiedRef = { id: string; titel: string };

/** Ein Programmpunkt des Scans: was dort steht und was das Verzeichnis dazu sagt. */
export type ProgrammZeile = {
  id: string;
  text: string;
  lied: LiedRef | null;
  /** false: das Lesen schlägt das Lied nur vor; die Redaktion übernimmt oder wählt anders. */
  sicher: boolean;
  kandidaten: LiedRef[];
};

/** Antwort des Servers (POST /api/auftritt-lesungen), Zeile für Zeile. */
export type LesVorschlag = { feld: Feld; wert: string; sicher: boolean };

export type LesProgrammpunkt = {
  text: string;
  sicher: boolean;
  lied: { id: string; titel: string; sicher: boolean } | null;
  kandidaten: LiedRef[];
};

export type KiZustand =
  | { status: "verfuegbar" }
  | { status: "nicht-verfuegbar"; meldung: string };

export type Zustand = {
  entwurfId: string;
  /** sammeln: das Formular ist offen. speichern: der Auftritt wird angelegt oder wurde es schon. */
  phase: "sammeln" | "speichern" | "gespeichert";
  werte: Werte;
  bearbeitet: Partial<Record<Feld, true>>;
  vorschlaege: Partial<Record<Feld, Sicherheit>>;
  programm: ProgrammZeile[];
  dateien: DateiEintrag[];
  ki: KiZustand;
  /** Gesetzt, sobald der Auftritt angelegt ist; ein erneutes Speichern legt ihn nicht noch einmal an. */
  auftrittId: string | null;
  programmGespeichert: boolean;
  fehler: string | null;
};

export type NeueDatei = {
  id: string;
  name: string;
  sizeBytes: number;
  art: DateiArt;
};

export type Ereignis =
  | { type: "dateien-abgelegt"; dateien: NeueDatei[] }
  | { type: "datei-inhalt-da"; dateiId: string }
  | { type: "datei-inhalt-fehlt"; dateiId: string }
  | { type: "datei-entfernt"; dateiId: string }
  | { type: "datei-art"; dateiId: string; art: DateiArt }
  | { type: "lesen-beginnt"; dateiId: string; anfrageId: number }
  | {
      type: "lesen-fertig";
      dateiId: string;
      anfrageId: number;
      felder: LesVorschlag[];
      programm: LesProgrammpunkt[];
    }
  | {
      type: "lesen-gescheitert";
      dateiId: string;
      anfrageId: number;
      meldung: string;
      verfuegbar: boolean;
    }
  | { type: "feld-geaendert"; feld: TextFeld; wert: string }
  | { type: "unsicher-geaendert"; wert: boolean }
  | { type: "zeile-gewaehlt"; zeileId: string; lied: LiedRef }
  | { type: "zeile-angenommen"; zeileId: string }
  | { type: "zeile-entfernt"; zeileId: string }
  | { type: "zeile-hinzugefuegt"; zeileId: string; lied: LiedRef }
  | { type: "zeile-verschoben"; zeileId: string; richtung: -1 | 1 }
  | { type: "speichern-beginnt" }
  | { type: "auftritt-angelegt"; auftrittId: string }
  | { type: "programm-gespeichert" }
  | { type: "upload"; dateiId: string; upload: UploadZustand }
  | { type: "speichern-gescheitert"; meldung: string }
  | { type: "gespeichert" }
  | { type: "wiederhergestellt"; zustand: Zustand };

export const leereWerte: Werte = {
  title: "",
  kind: "concert",
  jahr: "",
  monat: "",
  tag: "",
  unsicher: false,
  venue: "",
};

export function anfangszustand(entwurfId: string): Zustand {
  return {
    entwurfId,
    phase: "sammeln",
    werte: leereWerte,
    bearbeitet: {},
    vorschlaege: {},
    programm: [],
    dateien: [],
    ki: { status: "verfuegbar" },
    auftrittId: null,
    programmGespeichert: false,
    fehler: null,
  };
}

const bildEndungen = ["jpg", "jpeg", "png", "webp", "heic", "heif"];
const aufnahmeEndungen = [
  "mp4",
  "m4v",
  "mov",
  "webm",
  "mkv",
  "avi",
  "3gp",
  "mp3",
  "m4a",
  "wav",
  "flac",
  "ogg",
  "aac",
];

/** Die Art einer abgelegten Datei: Scans und Fotos sind das Programm, Video und Ton die Aufnahme. */
export function artAusDatei(name: string, contentType: string): DateiArt {
  const endung = name.toLowerCase().split(".").pop() ?? "";
  const typ = contentType.toLowerCase();
  if (
    typ === "application/pdf" ||
    typ.startsWith("image/") ||
    endung === "pdf" ||
    bildEndungen.includes(endung)
  )
    return "programm";
  if (
    typ.startsWith("video/") ||
    typ.startsWith("audio/") ||
    aufnahmeEndungen.includes(endung)
  )
    return "aufnahme";
  return "sonstiges";
}

/** Vorschläge, die die Felder des Formulars annehmen (Namen der Serverantwort). */
function istFeld(feld: string): feld is Feld {
  return (
    feld === "unsicher" || (textFelder as readonly string[]).includes(feld)
  );
}

function leer(werte: Werte, feld: Feld): boolean {
  return feld === "unsicher" ? !werte.unsicher : werte[feld] === "";
}

export type Pruefung = { titel?: string; datum?: string };

/** Dieselben Regeln wie das Formular "Neuer Auftritt" und der Server. */
export function pruefeWerte(werte: Werte): Pruefung {
  const fehler: Pruefung = {};
  if (!werte.title.trim()) fehler.titel = "Der Titel ist erforderlich.";
  const jahrText = werte.jahr.trim();
  const tagText = werte.tag.trim();
  const jahr = jahrText ? Number.parseInt(jahrText, 10) : null;
  const monat = werte.monat ? Number.parseInt(werte.monat, 10) : null;
  const tag = tagText ? Number.parseInt(tagText, 10) : null;
  if (
    jahrText &&
    (!/^\d+$/.test(jahrText) || jahr === null || jahr < 1800 || jahr > 2100)
  ) {
    fehler.datum = "Das Jahr liegt außerhalb des möglichen Bereichs.";
  } else if (tag !== null && monat === null) {
    fehler.datum = "Das Datum ist unvollständig.";
  } else if ((monat !== null || tag !== null) && jahr === null) {
    fehler.datum = "Das Datum ist unvollständig.";
  } else if (jahr !== null && monat !== null && tag !== null) {
    const tageImMonat = new Date(jahr, monat, 0).getDate();
    if (Number.isNaN(tag) || tag < 1 || tag > tageImMonat) {
      fehler.datum = "Das Datum passt nicht zum angegebenen Monat.";
    }
  }
  return fehler;
}

/** Speichern geht, wenn das Formular stimmt und nichts mehr offen ist, was die Redaktion entscheiden muss. */
export function kannSpeichern(zustand: Zustand): boolean {
  if (zustand.phase !== "sammeln") return false;
  const fehler = pruefeWerte(zustand.werte);
  if (fehler.titel || fehler.datum) return false;
  if (zustand.programm.some((zeile) => zeile.lied === null)) return false;
  if (zustand.dateien.some((datei) => datei.lesen.status === "liest"))
    return false;
  return zustand.dateien.every((datei) => datei.inhalt === "vorhanden");
}

function ersetzeDatei(
  zustand: Zustand,
  dateiId: string,
  aendern: (datei: DateiEintrag) => DateiEintrag,
): Zustand {
  if (!zustand.dateien.some((datei) => datei.id === dateiId)) return zustand;
  return {
    ...zustand,
    dateien: zustand.dateien.map((datei) =>
      datei.id === dateiId ? aendern(datei) : datei,
    ),
  };
}

function ersetzeZeile(
  zustand: Zustand,
  zeileId: string,
  aendern: (zeile: ProgrammZeile) => ProgrammZeile,
): Zustand {
  if (!zustand.programm.some((zeile) => zeile.id === zeileId)) return zustand;
  return {
    ...zustand,
    programm: zustand.programm.map((zeile) =>
      zeile.id === zeileId ? aendern(zeile) : zeile,
    ),
  };
}

function nimmLesung(
  zustand: Zustand,
  ereignis: Extract<Ereignis, { type: "lesen-fertig" }>,
): Zustand {
  const datei = zustand.dateien.find((d) => d.id === ereignis.dateiId);
  if (
    !datei ||
    datei.lesen.status !== "liest" ||
    datei.lesen.anfrageId !== ereignis.anfrageId
  )
    return zustand;

  const werte = { ...zustand.werte };
  const vorschlaege = { ...zustand.vorschlaege };
  for (const vorschlag of ereignis.felder) {
    const feld = vorschlag.feld;
    if (!istFeld(feld) || zustand.bearbeitet[feld]) continue;
    // "Art" beginnt mit einem Vorgabewert; erst ein Vorschlag macht sie besetzt.
    const frei =
      feld === "kind" ? vorschlaege.kind === undefined : leer(werte, feld);
    if (!frei) continue;
    if (feld === "unsicher") werte.unsicher = vorschlag.wert === "true";
    else werte[feld] = vorschlag.wert;
    vorschlaege[feld] = vorschlag.sicher ? "sicher" : "unsicher";
  }

  const neueZeilen: ProgrammZeile[] = ereignis.programm.map((punkt, index) => ({
    id: `${ereignis.dateiId}-${ereignis.anfrageId}-${index}`,
    text: punkt.text,
    lied: punkt.lied ? { id: punkt.lied.id, titel: punkt.lied.titel } : null,
    sicher: punkt.lied?.sicher ?? false,
    kandidaten: punkt.kandidaten,
  }));
  return {
    ...zustand,
    werte,
    vorschlaege,
    programm: [...zustand.programm, ...neueZeilen],
    dateien: zustand.dateien.map((d) =>
      d.id === ereignis.dateiId
        ? { ...d, lesen: { status: "gelesen", anfrageId: ereignis.anfrageId } }
        : d,
    ),
  };
}

export function reduziere(zustand: Zustand, ereignis: Ereignis): Zustand {
  switch (ereignis.type) {
    case "wiederhergestellt":
      return ereignis.zustand;

    case "dateien-abgelegt": {
      if (zustand.phase !== "sammeln") return zustand;
      const neu: DateiEintrag[] = ereignis.dateien
        .filter(
          (d) => !zustand.dateien.some((vorhanden) => vorhanden.id === d.id),
        )
        .map((d) => ({
          id: d.id,
          name: d.name,
          sizeBytes: d.sizeBytes,
          art: d.art,
          inhalt: "vorhanden",
          lesen: { status: "keine" },
          upload: { status: "wartend" },
        }));
      return neu.length === 0
        ? zustand
        : { ...zustand, dateien: [...zustand.dateien, ...neu] };
    }

    case "datei-inhalt-da":
      return ersetzeDatei(zustand, ereignis.dateiId, (d) =>
        d.inhalt === "vorhanden" ? d : { ...d, inhalt: "vorhanden" },
      );

    case "datei-inhalt-fehlt":
      return ersetzeDatei(zustand, ereignis.dateiId, (d) => ({
        ...d,
        inhalt: "fehlt",
      }));

    case "datei-entfernt":
      if (zustand.phase !== "sammeln") return zustand;
      return {
        ...zustand,
        dateien: zustand.dateien.filter((d) => d.id !== ereignis.dateiId),
      };

    case "datei-art":
      if (zustand.phase !== "sammeln") return zustand;
      return ersetzeDatei(zustand, ereignis.dateiId, (d) => ({
        ...d,
        art: ereignis.art,
      }));

    case "lesen-beginnt":
      return ersetzeDatei(zustand, ereignis.dateiId, (d) => ({
        ...d,
        lesen: { status: "liest", anfrageId: ereignis.anfrageId },
      }));

    case "lesen-fertig":
      return nimmLesung(zustand, ereignis);

    case "lesen-gescheitert": {
      const datei = zustand.dateien.find((d) => d.id === ereignis.dateiId);
      if (
        !datei ||
        datei.lesen.status !== "liest" ||
        datei.lesen.anfrageId !== ereignis.anfrageId
      )
        return zustand;
      const lesen: LesenZustand = ereignis.verfuegbar
        ? {
            status: "fehlgeschlagen",
            anfrageId: ereignis.anfrageId,
            meldung: ereignis.meldung,
          }
        : {
            status: "nicht-verfuegbar",
            anfrageId: ereignis.anfrageId,
            meldung: ereignis.meldung,
          };
      return {
        ...ersetzeDatei(zustand, ereignis.dateiId, (d) => ({ ...d, lesen })),
        ki: ereignis.verfuegbar
          ? zustand.ki
          : { status: "nicht-verfuegbar", meldung: ereignis.meldung },
      };
    }

    case "feld-geaendert": {
      if (zustand.phase !== "sammeln") return zustand;
      const vorschlaege = { ...zustand.vorschlaege };
      delete vorschlaege[ereignis.feld];
      return {
        ...zustand,
        werte: { ...zustand.werte, [ereignis.feld]: ereignis.wert },
        bearbeitet: { ...zustand.bearbeitet, [ereignis.feld]: true },
        vorschlaege,
      };
    }

    case "unsicher-geaendert": {
      if (zustand.phase !== "sammeln") return zustand;
      const vorschlaege = { ...zustand.vorschlaege };
      delete vorschlaege.unsicher;
      return {
        ...zustand,
        werte: { ...zustand.werte, unsicher: ereignis.wert },
        bearbeitet: { ...zustand.bearbeitet, unsicher: true },
        vorschlaege,
      };
    }

    case "zeile-gewaehlt":
      if (zustand.phase !== "sammeln") return zustand;
      return ersetzeZeile(zustand, ereignis.zeileId, (zeile) => ({
        ...zeile,
        lied: ereignis.lied,
        sicher: true,
      }));

    case "zeile-angenommen":
      if (zustand.phase !== "sammeln") return zustand;
      return ersetzeZeile(zustand, ereignis.zeileId, (zeile) =>
        zeile.lied ? { ...zeile, sicher: true } : zeile,
      );

    case "zeile-entfernt":
      if (zustand.phase !== "sammeln") return zustand;
      return {
        ...zustand,
        programm: zustand.programm.filter(
          (zeile) => zeile.id !== ereignis.zeileId,
        ),
      };

    case "zeile-hinzugefuegt":
      if (zustand.phase !== "sammeln") return zustand;
      return {
        ...zustand,
        programm: [
          ...zustand.programm,
          {
            id: ereignis.zeileId,
            text: ereignis.lied.titel,
            lied: ereignis.lied,
            sicher: true,
            kandidaten: [],
          },
        ],
      };

    case "zeile-verschoben": {
      if (zustand.phase !== "sammeln") return zustand;
      const von = zustand.programm.findIndex(
        (zeile) => zeile.id === ereignis.zeileId,
      );
      const nach = von + ereignis.richtung;
      if (von < 0 || nach < 0 || nach >= zustand.programm.length)
        return zustand;
      const programm = [...zustand.programm];
      [programm[von], programm[nach]] = [programm[nach], programm[von]];
      return { ...zustand, programm };
    }

    case "speichern-beginnt":
      if (zustand.phase === "gespeichert") return zustand;
      return { ...zustand, phase: "speichern", fehler: null };

    case "auftritt-angelegt":
      return { ...zustand, auftrittId: ereignis.auftrittId };

    case "programm-gespeichert":
      return { ...zustand, programmGespeichert: true };

    case "upload":
      return ersetzeDatei(zustand, ereignis.dateiId, (d) => ({
        ...d,
        upload: ereignis.upload,
      }));

    case "speichern-gescheitert":
      return { ...zustand, phase: "speichern", fehler: ereignis.meldung };

    case "gespeichert":
      return { ...zustand, phase: "gespeichert", fehler: null };
  }
}
