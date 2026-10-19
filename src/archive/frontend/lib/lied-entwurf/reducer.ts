import { ausDateiname, type DateiTyp } from "./dateinamen.ts";

export type { DateiTyp } from "./dateinamen.ts";

// Der Entwurf eines Liedes aus abgelegten Dateien (#175, Pilot von #159).
//
// Die Schnittstelle sind Zustand, Ereignis und `reduziere`: eine reine
// Funktion ohne Netz, Zeit oder React. Hochladen, Anlegen und Abbrechen laufen
// im Hook (`use-lied-entwurf.ts`) und melden sich mit Ereignissen zurück. Jedes
// Ergebnis nennt den Entwurf (`entwurfId`) und, wo es sie gibt, die Anfrage
// oder den Versuch; was zu einem anderen Entwurf oder einem überholten Versuch
// gehört, ändert nichts (der Zustand bleibt dasselbe Objekt).
//
// Welche Felder die Redaktion angefasst hat, gehört nur zum Entwurf. Es wird
// nie gespeichert und nie als Herkunft angezeigt (ADR-0001).

export type LiedFeld = "title";

export type DateiFeld = "art" | "stimme";

export type UploadZustand =
  | { status: "wartend" }
  | { status: "laedt"; versuch: number; bytes: number; gesamt: number }
  | { status: "hochgeladen"; versuch: number; serverDateiId: string }
  | { status: "fehlgeschlagen"; versuch: number; meldung: string };

export type DateiEntwurf = {
  id: string;
  name: string;
  sizeBytes: number;
  art: DateiTyp;
  stimme: string;
  bearbeitet: Record<DateiFeld, boolean>;
  upload: UploadZustand;
};

type Kern = {
  entwurfId: string;
  lied: Record<LiedFeld, string>;
  bearbeitet: Record<LiedFeld, boolean>;
  dateien: DateiEntwurf[];
};

export type Zustand =
  | { phase: "leer"; entwurfId: string }
  | (Kern & { phase: "sammeln"; fehler: string | null })
  | (Kern & { phase: "speichern"; anfrageId: number; liedId: string | null })
  | (Kern & { phase: "gespeichert"; anfrageId: number; liedId: string })
  | { phase: "verworfen"; entwurfId: string };

export type NeueDatei = { id: string; name: string; sizeBytes: number };

export type Ereignis =
  | { type: "entwurf-begonnen"; entwurfId: string }
  | { type: "entwurf-verworfen"; entwurfId: string }
  | { type: "dateien-abgelegt"; entwurfId: string; dateien: NeueDatei[] }
  | {
      type: "lied-feld-geaendert";
      entwurfId: string;
      feld: LiedFeld;
      wert: string;
    }
  | ({
      type: "datei-feld-geaendert";
      entwurfId: string;
      dateiId: string;
    } & ({ feld: "art"; wert: DateiTyp } | { feld: "stimme"; wert: string }))
  | { type: "datei-entfernt"; entwurfId: string; dateiId: string }
  | { type: "speichern-angefordert"; entwurfId: string; anfrageId: number }
  | {
      type: "lied-angelegt";
      entwurfId: string;
      anfrageId: number;
      liedId: string;
    }
  | {
      type: "lied-anlegen-fehlgeschlagen";
      entwurfId: string;
      anfrageId: number;
      meldung: string;
    }
  | {
      type: "wiederholen-angefordert";
      entwurfId: string;
      dateiId: string;
      versuch: number;
    }
  | {
      type: "upload-gestartet";
      entwurfId: string;
      dateiId: string;
      versuch: number;
    }
  | {
      type: "upload-fortschritt";
      entwurfId: string;
      dateiId: string;
      versuch: number;
      bytes: number;
      gesamt: number;
    }
  | {
      type: "upload-erfolgreich";
      entwurfId: string;
      dateiId: string;
      versuch: number;
      serverDateiId: string;
    }
  | {
      type: "upload-fehlgeschlagen";
      entwurfId: string;
      dateiId: string;
      versuch: number;
      meldung: string;
    };

export function anfangszustand(entwurfId: string): Zustand {
  return { phase: "leer", entwurfId };
}

function neueDatei(datei: NeueDatei): DateiEntwurf {
  const gelesen = ausDateiname(datei.name);
  return {
    id: datei.id,
    name: datei.name,
    sizeBytes: datei.sizeBytes,
    art: gelesen.art,
    stimme: gelesen.stimme,
    bearbeitet: { art: false, stimme: false },
    upload: { status: "wartend" },
  };
}

function abgelegt(zustand: Zustand, neue: NeueDatei[]): Zustand {
  if (neue.length === 0) return zustand;
  if (zustand.phase !== "leer" && zustand.phase !== "sammeln") return zustand;
  const dateien = neue.map(neueDatei);
  if (zustand.phase === "leer") {
    return {
      phase: "sammeln",
      entwurfId: zustand.entwurfId,
      lied: { title: ausDateiname(neue[0].name).titel },
      bearbeitet: { title: false },
      dateien,
      fehler: null,
    };
  }
  // Ein Titel wird nur vorgeschlagen, solange ihn niemand angefasst hat.
  const titel =
    !zustand.bearbeitet.title && zustand.lied.title === ""
      ? ausDateiname(neue[0].name).titel
      : zustand.lied.title;
  return {
    ...zustand,
    lied: { ...zustand.lied, title: titel },
    dateien: [...zustand.dateien, ...dateien],
  };
}

function dateiAendern(
  zustand: Extract<Zustand, { phase: "sammeln" }>,
  dateiId: string,
  aendern: (datei: DateiEntwurf) => DateiEntwurf,
): Zustand {
  if (!zustand.dateien.some((d) => d.id === dateiId)) return zustand;
  return {
    ...zustand,
    dateien: zustand.dateien.map((d) => (d.id === dateiId ? aendern(d) : d)),
  };
}

type MitLied = Kern & { anfrageId: number; liedId: string };

/**
 * Ersetzt die Dateien eines Liedes, das schon angelegt ist. Solange eine Datei
 * noch wartet oder lädt, wird gespeichert; sind alle hoch oder gescheitert,
 * ist der Entwurf gespeichert. Eine gescheiterte Datei öffnet ihn beim
 * Wiederholen wieder.
 */
function mitDateien(zustand: MitLied, dateien: DateiEntwurf[]): Zustand {
  const fertig = dateien.every(
    (d) =>
      d.upload.status === "hochgeladen" || d.upload.status === "fehlgeschlagen",
  );
  return {
    ...zustand,
    phase: fertig ? "gespeichert" : "speichern",
    dateien,
  };
}

function mitLied(zustand: Zustand): MitLied | null {
  if (zustand.phase === "gespeichert") return zustand;
  if (zustand.phase === "speichern" && zustand.liedId !== null) {
    return { ...zustand, liedId: zustand.liedId };
  }
  return null;
}

/** Setzt `upload` einer Datei, wenn ihr Versuch der erwartete ist. */
function uploadAendern(
  zustand: Zustand,
  dateiId: string,
  versuch: number | null,
  aus: (aktuell: UploadZustand) => UploadZustand | null,
): Zustand {
  const lied = mitLied(zustand);
  if (lied === null) return zustand;
  const datei = lied.dateien.find((d) => d.id === dateiId);
  if (!datei) return zustand;
  const aktuell = datei.upload;
  if (
    versuch !== null &&
    aktuell.status !== "wartend" &&
    aktuell.versuch !== versuch
  ) {
    return zustand;
  }
  const neu = aus(aktuell);
  if (neu === null) return zustand;
  return mitDateien(
    lied,
    lied.dateien.map((d) => (d.id === dateiId ? { ...d, upload: neu } : d)),
  );
}

function nichtErschoepft(x: never): never {
  throw new Error(`Unbehandeltes Ereignis: ${JSON.stringify(x)}`);
}

export function reduziere(zustand: Zustand, ereignis: Ereignis): Zustand {
  if (ereignis.type === "entwurf-begonnen") {
    return anfangszustand(ereignis.entwurfId);
  }
  // Alles andere gilt nur dem Entwurf, der gerade offen ist.
  if (ereignis.entwurfId !== zustand.entwurfId) return zustand;
  if (zustand.phase === "verworfen") return zustand;

  switch (ereignis.type) {
    case "entwurf-verworfen":
      return { phase: "verworfen", entwurfId: zustand.entwurfId };

    case "dateien-abgelegt":
      return abgelegt(zustand, ereignis.dateien);

    case "lied-feld-geaendert":
      if (zustand.phase !== "sammeln") return zustand;
      return {
        ...zustand,
        lied: { ...zustand.lied, [ereignis.feld]: ereignis.wert },
        bearbeitet: { ...zustand.bearbeitet, [ereignis.feld]: true },
      };

    case "datei-feld-geaendert":
      if (zustand.phase !== "sammeln") return zustand;
      return dateiAendern(zustand, ereignis.dateiId, (datei) => {
        if (ereignis.feld === "art") {
          return {
            ...datei,
            art: ereignis.wert,
            stimme: ereignis.wert === "partitur" ? "" : datei.stimme,
            bearbeitet: { ...datei.bearbeitet, art: true },
          };
        }
        return {
          ...datei,
          stimme: ereignis.wert,
          bearbeitet: { ...datei.bearbeitet, stimme: true },
        };
      });

    case "datei-entfernt": {
      if (zustand.phase === "sammeln") {
        if (!zustand.dateien.some((d) => d.id === ereignis.dateiId)) {
          return zustand;
        }
        return {
          ...zustand,
          dateien: zustand.dateien.filter((d) => d.id !== ereignis.dateiId),
        };
      }
      // Nach dem Anlegen kann nur eine gescheiterte Datei weg.
      const lied = mitLied(zustand);
      const datei = lied?.dateien.find((d) => d.id === ereignis.dateiId);
      if (!lied || datei?.upload.status !== "fehlgeschlagen") return zustand;
      return mitDateien(
        lied,
        lied.dateien.filter((d) => d.id !== ereignis.dateiId),
      );
    }

    case "speichern-angefordert":
      if (zustand.phase !== "sammeln" || !kannSpeichern(zustand)) {
        return zustand;
      }
      return {
        phase: "speichern",
        entwurfId: zustand.entwurfId,
        lied: zustand.lied,
        bearbeitet: zustand.bearbeitet,
        dateien: zustand.dateien,
        anfrageId: ereignis.anfrageId,
        liedId: null,
      };

    case "lied-angelegt":
      if (
        zustand.phase !== "speichern" ||
        zustand.liedId !== null ||
        zustand.anfrageId !== ereignis.anfrageId
      ) {
        return zustand;
      }
      return { ...zustand, liedId: ereignis.liedId };

    case "lied-anlegen-fehlgeschlagen":
      if (
        zustand.phase !== "speichern" ||
        zustand.liedId !== null ||
        zustand.anfrageId !== ereignis.anfrageId
      ) {
        return zustand;
      }
      return {
        phase: "sammeln",
        entwurfId: zustand.entwurfId,
        lied: zustand.lied,
        bearbeitet: zustand.bearbeitet,
        dateien: zustand.dateien,
        fehler: ereignis.meldung,
      };

    case "wiederholen-angefordert":
      return uploadAendern(zustand, ereignis.dateiId, null, (aktuell) =>
        aktuell.status === "fehlgeschlagen" &&
        ereignis.versuch > aktuell.versuch
          ? {
              status: "laedt",
              versuch: ereignis.versuch,
              bytes: 0,
              gesamt: 0,
            }
          : null,
      );

    case "upload-gestartet":
      if (zustand.phase !== "speichern") return zustand;
      return uploadAendern(zustand, ereignis.dateiId, null, (aktuell) => {
        // Ein Neustart gilt nur mit einem höheren Versuch als dem letzten
        // und nie für eine Datei, die schon oben ist.
        if (aktuell.status === "hochgeladen") return null;
        if (
          aktuell.status !== "wartend" &&
          ereignis.versuch <= aktuell.versuch
        ) {
          return null;
        }
        return {
          status: "laedt",
          versuch: ereignis.versuch,
          bytes: 0,
          gesamt: 0,
        };
      });

    case "upload-fortschritt":
      return uploadAendern(
        zustand,
        ereignis.dateiId,
        ereignis.versuch,
        (aktuell) =>
          aktuell.status === "laedt"
            ? { ...aktuell, bytes: ereignis.bytes, gesamt: ereignis.gesamt }
            : null,
      );

    case "upload-erfolgreich":
      return uploadAendern(
        zustand,
        ereignis.dateiId,
        ereignis.versuch,
        (aktuell) =>
          aktuell.status === "laedt"
            ? {
                status: "hochgeladen",
                versuch: ereignis.versuch,
                serverDateiId: ereignis.serverDateiId,
              }
            : null,
      );

    case "upload-fehlgeschlagen":
      return uploadAendern(
        zustand,
        ereignis.dateiId,
        ereignis.versuch,
        (aktuell) =>
          aktuell.status === "laedt"
            ? {
                status: "fehlgeschlagen",
                versuch: ereignis.versuch,
                meldung: ereignis.meldung,
              }
            : null,
      );

    default:
      return nichtErschoepft(ereignis);
  }
}

// Abgeleitetes wird gelesen, nicht gespeichert.

/** Ein Titel und mindestens eine Datei genügen. */
export function kannSpeichern(zustand: Zustand): boolean {
  return (
    zustand.phase === "sammeln" &&
    zustand.lied.title.trim().length > 0 &&
    zustand.dateien.length > 0
  );
}

export function fehlgeschlagene(
  zustand: Zustand,
): { id: string; name: string; meldung: string }[] {
  if (zustand.phase !== "speichern" && zustand.phase !== "gespeichert") {
    return [];
  }
  return zustand.dateien.flatMap((d) =>
    d.upload.status === "fehlgeschlagen"
      ? [{ id: d.id, name: d.name, meldung: d.upload.meldung }]
      : [],
  );
}

/** Die Dateien, die auf dem Server liegen, in der Reihenfolge des Entwurfs. */
export function hochgeladene(
  zustand: Zustand,
): { id: string; name: string; serverDateiId: string }[] {
  if (zustand.phase !== "speichern" && zustand.phase !== "gespeichert") {
    return [];
  }
  return zustand.dateien.flatMap((d) =>
    d.upload.status === "hochgeladen"
      ? [{ id: d.id, name: d.name, serverDateiId: d.upload.serverDateiId }]
      : [],
  );
}
