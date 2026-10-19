"use client";

import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { inhaltstypFuerDatei, ladeDateiHoch } from "@/lib/dateien";
import {
  ladeEntwurf,
  speichereEntwurf,
  verwirfEntwurf,
} from "@/lib/lied-entwuerfe";
import { istPdf, LesenFehler, leseEntwurfPdf } from "@/lib/lied-lesen";
import { fetchLiedSuche, type LiedEingabe, legeLiedAn } from "@/lib/lieder";
import {
  AbbruchFehler,
  MaterialFehler,
  problemTitel,
} from "@/lib/uebertragung";
import {
  alsGespeichert,
  type Gespeichert,
  gespeichertesLesen,
  lesungsEreignisse,
  wiederhergestellt,
} from "./gespeichert";
import {
  anfangszustand,
  type DateiEntwurf,
  type DateiFeld,
  type DateiTyp,
  type Ereignis,
  kannSpeichern,
  type LiedFeld,
  reduziere,
  titelSchluessel,
  type Zustand,
} from "./reducer";

// Der Hook führt aus, was der Reducer nicht darf: das Lied anlegen, die Dateien
// hochladen, abbrechen. Jedes Ergebnis geht mit Entwurf, Anfrage oder Versuch
// zurück an den Reducer, der über Veraltetes selbst entscheidet.
//
// "Angefangen" (#178): Der Hook hält den Entwurf nach jeder Änderung im
// Backend fest und fängt beim Fortsetzen mit dem dort gespeicherten Entwurf als
// Anfangszustand desselben Reducers an. Was der Server inzwischen gelesen hat,
// kommt als gewöhnliche Lese-Ereignisse an.

/** Wie lange nach der letzten Änderung gewartet wird, bevor sie gespeichert wird. */
const SICHERN_NACH_MS = 400;
const ABFRAGE_MS = 2000;
const ABFRAGE_MAX = 120;
/** Wie lange nach der letzten Titeländerung gewartet wird, bevor nach einem gleichnamigen Lied gesucht wird. */
const SUCHEN_NACH_MS = 300;

/** Das Lied, zu dem die Lied-Seite Dateien hinzufügt. */
export type FestesLied = { liedId: string; titel: string };

const LEER: LiedEingabe = {
  title: "",
  composer: "",
  arranger: "",
  lyricist: "",
  voiceConfiguration: "",
  musicalKey: "",
  language: "",
  lyrics: "",
  occasion: "",
};

function neueId(): string {
  return crypto.randomUUID();
}

async function fehlerText(ursache: unknown): Promise<string> {
  if (ursache instanceof MaterialFehler || ursache instanceof LesenFehler) {
    return ursache.message;
  }
  return await problemTitel(
    ursache,
    "Das hat nicht geklappt. Bitte erneut versuchen.",
  );
}

function hochladeArt(datei: DateiEntwurf, original: File) {
  const partitur = datei.art === "partitur";
  const kind = partitur
    ? "noten"
    : (datei.art as Exclude<DateiTyp, "partitur">);
  return {
    kind,
    isPartitur: partitur,
    stimme: datei.stimme.trim(),
    contentType:
      kind === "noten" ? "application/pdf" : inhaltstypFuerDatei(original),
    dateiName: original.name,
  };
}

function hoechsteAnfrage(gespeichert: Gespeichert | undefined): number {
  return Math.max(
    0,
    ...(gespeichert?.dateien ?? []).flatMap((d) => [
      d.lesen.anfrageId,
      d.lesung?.anfrageId ?? 0,
    ]),
  );
}

/**
 * Der Entwurf der Redaktion. Mit `gespeichert` (aus "Angefangen") beginnt der
 * Reducer in dem Zustand, den das Backend festhält; sonst leer.
 */
export function useLiedEntwurf(
  gespeichert?: Gespeichert,
  festesLied?: FestesLied,
) {
  // Auf der Lied-Seite gibt es kein Formular und keinen angefangenen Entwurf.
  const fest = festesLied !== undefined;
  const [start] = useState(() =>
    gespeichert ? wiederhergestellt(gespeichert) : null,
  );
  const [zustand, dispatch] = useReducer(
    reduziere,
    undefined,
    (): Zustand =>
      start?.zustand ??
      anfangszustand(
        neueId(),
        festesLied
          ? {
              status: "vorhanden",
              liedId: festesLied.liedId,
              titel: festesLied.titel,
              fest: true,
            }
          : undefined,
      ),
  );
  // Der Reducer ist rein: dieselbe Faltung hier und in React ergibt denselben
  // Zustand, und die Ablaufsteuerung sieht ihn ohne Rendern.
  const zustandRef = useRef(zustand);
  const dateien = useRef(new Map<string, File>());
  const anfragen = useRef(0);
  const abbruch = useRef<AbortController | null>(null);
  const lesungen = useRef(new Map<string, AbortController>());
  const lesAnfragen = useRef(hoechsteAnfrage(gespeichert));
  // Gespeichert im Backend (#178)
  const gespeichertImBackend = useRef(gespeichert !== undefined);
  const sicherungGesperrt = useRef(false);
  const sicherungOffen = useRef(false);
  const sicherungTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const sicherungKette = useRef<Promise<void>>(Promise.resolve());
  const lebt = useRef(true);

  /** Schickt den aktuellen Entwurf ans Backend; nacheinander, der letzte Stand gilt. */
  const sichernJetzt = useCallback((schliessend = false): Promise<void> => {
    if (sicherungTimer.current !== null) clearTimeout(sicherungTimer.current);
    sicherungTimer.current = null;
    sicherungKette.current = sicherungKette.current.then(async () => {
      if (sicherungGesperrt.current) return;
      const aktuell = zustandRef.current;
      try {
        if (aktuell.phase === "sammeln" && aktuell.dateien.length === 0) {
          // Ohne Dateien bleibt nichts Angefangenes.
          if (gespeichertImBackend.current) {
            gespeichertImBackend.current = false;
            await verwirfEntwurf(aktuell.entwurfId);
          }
          sicherungOffen.current = false;
          return;
        }
        const abzug = alsGespeichert(aktuell);
        if (!abzug) return;
        sicherungOffen.current = false;
        gespeichertImBackend.current = true;
        await speichereEntwurf(abzug, schliessend);
      } catch {
        // Das Speichern ist ein Nebenweg: das Formular arbeitet weiter.
        sicherungOffen.current = true;
      }
    });
    return sicherungKette.current;
  }, []);

  const sicherungPlanen = useCallback(() => {
    sicherungOffen.current = true;
    if (sicherungTimer.current !== null) clearTimeout(sicherungTimer.current);
    sicherungTimer.current = setTimeout(
      () => void sichernJetzt(),
      SICHERN_NACH_MS,
    );
  }, [sichernJetzt]);

  /** Jedes Ereignis geht durch den Reducer; was den Entwurf ändert, wird gesichert. */
  const sende = useCallback(
    (ereignis: Ereignis) => {
      const vorher = zustandRef.current;
      const nachher = reduziere(vorher, ereignis);
      zustandRef.current = nachher;
      dispatch(ereignis);
      if (nachher !== vorher && nachher.phase === "sammeln" && !fest) {
        sicherungPlanen();
      }
    },
    [fest, sicherungPlanen],
  );

  const lesungenAbbrechen = useCallback((dateiId?: string) => {
    for (const [id, steuerung] of lesungen.current) {
      if (dateiId === undefined || id === dateiId) {
        steuerung.abort();
        lesungen.current.delete(id);
      }
    }
  }, []);

  useEffect(() => {
    lebt.current = true;
    const verlassen = () => {
      if (sicherungOffen.current) void sichernJetzt(true);
    };
    const sichtbarkeit = () => {
      if (document.visibilityState === "hidden") verlassen();
    };
    window.addEventListener("pagehide", verlassen);
    document.addEventListener("visibilitychange", sichtbarkeit);
    return () => {
      lebt.current = false;
      window.removeEventListener("pagehide", verlassen);
      document.removeEventListener("visibilitychange", sichtbarkeit);
      verlassen();
      abbruch.current?.abort();
      lesungenAbbrechen();
    };
  }, [lesungenAbbrechen, sichernJetzt]);

  /**
   * Fortsetzen: was der Server inzwischen gelesen hat, kommt als gewöhnliche
   * Ereignisse an; läuft ein Lesen noch dort, wird nachgefragt.
   */
  useEffect(() => {
    if (!start) return;
    let beendet = false;
    for (const ereignis of start.ereignisse) sende(ereignis);
    let laeuft = start.laeuft;
    const abfragen = async () => {
      for (let runde = 0; runde < ABFRAGE_MAX && laeuft.length > 0; runde++) {
        await new Promise((fertig) => setTimeout(fertig, ABFRAGE_MS));
        if (beendet) return;
        const aktuell = zustandRef.current;
        if (aktuell.phase !== "sammeln") return;
        let frisch: Gespeichert;
        try {
          frisch = await ladeEntwurf(aktuell.entwurfId);
        } catch {
          continue;
        }
        if (beendet || zustandRef.current.phase !== "sammeln") return;
        const naechste: string[] = [];
        for (const datei of frisch.dateien) {
          if (!laeuft.includes(datei.id)) continue;
          const bekannt = zustandRef.current.dateien.find(
            (d) => d.id === datei.id,
          );
          if (!bekannt) continue;
          const aus = lesungsEreignisse(aktuell.entwurfId, {
            ...datei,
            lesen: gespeichertesLesen(bekannt.lesen),
          });
          for (const ereignis of aus.ereignisse) sende(ereignis);
          if (aus.laeuft) naechste.push(datei.id);
        }
        laeuft = naechste;
      }
    };
    void abfragen();
    return () => {
      beendet = true;
    };
  }, [start, sende]);

  /** Liest eine PDF; jede Anfrage bekommt eine neue Nummer und überholt die alte. */
  const lesen = useCallback(
    async (dateiId: string) => {
      const original = dateien.current.get(dateiId);
      if (!original) return;
      const entwurfId = zustandRef.current.entwurfId;
      const anfrageId = ++lesAnfragen.current;
      lesungenAbbrechen(dateiId);
      const steuerung = new AbortController();
      lesungen.current.set(dateiId, steuerung);
      sende({ type: "lesen-angefordert", entwurfId, dateiId, anfrageId });
      // Die Datei muss im Backend stehen, bevor es das Ergebnis für sie hält.
      await sichernJetzt();
      try {
        const felder = await leseEntwurfPdf(
          entwurfId,
          anfrageId,
          original,
          steuerung.signal,
          dateiId,
        );
        sende({
          type: "lesen-ergebnis",
          entwurfId,
          dateiId,
          anfrageId,
          felder,
        });
      } catch (ursache) {
        if (ursache instanceof AbbruchFehler) return;
        sende({
          type: "lesen-fehlgeschlagen",
          entwurfId,
          dateiId,
          anfrageId,
          meldung: await fehlerText(ursache),
          verfuegbar: !(ursache instanceof LesenFehler && !ursache.verfuegbar),
        });
      }
    },
    [lesungenAbbrechen, sende, sichernJetzt],
  );

  const ablegen = useCallback(
    (gewaehlte: File[]) => {
      const entwurfId = zustandRef.current.entwurfId;
      const neu: File[] = [];
      for (const datei of gewaehlte) {
        // Eine wiederhergestellte Datei, die wieder auftaucht, ist dieselbe.
        const aktuell = zustandRef.current;
        const fehlend =
          aktuell.phase === "sammeln"
            ? aktuell.dateien.find(
                (d) =>
                  d.inhalt === "fehlt" &&
                  d.name === datei.name &&
                  d.sizeBytes === datei.size &&
                  !dateien.current.has(d.id),
              )
            : undefined;
        if (fehlend) {
          dateien.current.set(fehlend.id, datei);
          sende({
            type: "datei-nachgereicht",
            entwurfId,
            dateiId: fehlend.id,
          });
        } else {
          neu.push(datei);
        }
      }
      if (neu.length === 0) return;
      const abgelegt = neu.map((datei) => {
        const id = neueId();
        dateien.current.set(id, datei);
        return { id, name: datei.name, sizeBytes: datei.size };
      });
      sende({ type: "dateien-abgelegt", entwurfId, dateien: abgelegt });
      abgelegt.forEach((datei, index) => {
        if (!fest && istPdf(neu[index])) void lesen(datei.id);
      });
    },
    [fest, lesen, sende],
  );

  // Passt der Titel zu einem vorhandenen Lied, wird es angeboten (#179). Die
  // Suche läuft hier, ihr Ergebnis nennt Entwurf und Titel; über Veraltetes
  // entscheidet der Reducer.
  const suchTitel = zustand.phase === "sammeln" ? zustand.lied.title : "";
  const suchbar =
    zustand.phase === "sammeln" &&
    (zustand.ziel.status === "neu" || zustand.ziel.status === "abgelehnt");
  const suchEntwurfId = zustand.entwurfId;
  useEffect(() => {
    if (!suchbar || suchTitel.trim() === "") return;
    const steuerung = new AbortController();
    const timer = setTimeout(() => {
      fetchLiedSuche(suchTitel.trim(), 1, steuerung.signal).then(
        (liste) => {
          const treffer = liste.lieder.find(
            (lied) =>
              titelSchluessel(lied.title) === titelSchluessel(suchTitel),
          );
          if (!treffer || steuerung.signal.aborted) return;
          sende({
            type: "treffer-gefunden",
            entwurfId: suchEntwurfId,
            titel: suchTitel,
            liedId: treffer.id,
            liedTitel: treffer.title,
          });
        },
        // Ohne Suche bleibt es beim neuen Lied.
        () => undefined,
      );
    }, SUCHEN_NACH_MS);
    return () => {
      clearTimeout(timer);
      steuerung.abort();
    };
  }, [suchbar, suchTitel, suchEntwurfId, sende]);

  const trefferAnnehmen = useCallback(() => {
    sende({
      type: "treffer-angenommen",
      entwurfId: zustandRef.current.entwurfId,
    });
  }, [sende]);

  const trefferAblehnen = useCallback(() => {
    sende({
      type: "treffer-abgelehnt",
      entwurfId: zustandRef.current.entwurfId,
    });
  }, [sende]);

  const liedFeldAendern = useCallback(
    (feld: LiedFeld, wert: string) => {
      sende({
        type: "lied-feld-geaendert",
        entwurfId: zustandRef.current.entwurfId,
        feld,
        wert,
      });
    },
    [sende],
  );

  const dateiFeldAendern = useCallback(
    (dateiId: string, feld: DateiFeld, wert: string) => {
      const entwurfId = zustandRef.current.entwurfId;
      sende(
        feld === "art"
          ? {
              type: "datei-feld-geaendert",
              entwurfId,
              dateiId,
              feld,
              wert: wert as DateiTyp,
            }
          : { type: "datei-feld-geaendert", entwurfId, dateiId, feld, wert },
      );
    },
    [sende],
  );

  const dateiEntfernen = useCallback(
    (dateiId: string) => {
      lesungenAbbrechen(dateiId);
      dateien.current.delete(dateiId);
      sende({
        type: "datei-entfernt",
        entwurfId: zustandRef.current.entwurfId,
        dateiId,
      });
    },
    [lesungenAbbrechen, sende],
  );

  /** Verwirft den Entwurf, auch im Backend; spätere Ergebnisse ändern nichts. */
  const verwerfen = useCallback(async () => {
    const entwurfId = zustandRef.current.entwurfId;
    abbruch.current?.abort();
    lesungenAbbrechen();
    sicherungGesperrt.current = true;
    if (sicherungTimer.current !== null) clearTimeout(sicherungTimer.current);
    sicherungTimer.current = null;
    sende({ type: "entwurf-verworfen", entwurfId });
    await sicherungKette.current;
    if (gespeichertImBackend.current) {
      gespeichertImBackend.current = false;
      try {
        await verwirfEntwurf(entwurfId);
      } catch {
        // Der Eintrag bleibt in "Angefangen" und lässt sich dort verwerfen.
      }
    }
  }, [lesungenAbbrechen, sende]);

  /** Lädt eine Datei hoch; false, wenn der Entwurf abgebrochen wurde. */
  const hochladen = useCallback(
    async (
      entwurfId: string,
      liedId: string,
      datei: DateiEntwurf,
      versuch: number,
      signal: AbortSignal,
    ): Promise<boolean> => {
      const original = dateien.current.get(datei.id);
      if (!original) return true;
      try {
        const gespeichertDatei = await ladeDateiHoch(
          liedId,
          hochladeArt(datei, original),
          original,
          undefined,
          {
            signal,
            onFortschritt: (bytes, gesamt) =>
              sende({
                type: "upload-fortschritt",
                entwurfId,
                dateiId: datei.id,
                versuch,
                bytes,
                gesamt,
              }),
          },
        );
        sende({
          type: "upload-erfolgreich",
          entwurfId,
          dateiId: datei.id,
          versuch,
          serverDateiId: gespeichertDatei.id,
        });
      } catch (ursache) {
        if (ursache instanceof AbbruchFehler) return false;
        sende({
          type: "upload-fehlgeschlagen",
          entwurfId,
          dateiId: datei.id,
          versuch,
          meldung: await fehlerText(ursache),
        });
      }
      return true;
    },
    [sende],
  );

  const speichern = useCallback(async () => {
    const aktuell = zustandRef.current;
    if (aktuell.phase !== "sammeln" || !kannSpeichern(aktuell)) return;
    const { entwurfId } = aktuell;
    const anfrageId = ++anfragen.current;
    const steuerung = new AbortController();
    abbruch.current = steuerung;
    const liste = aktuell.dateien;
    // Was jetzt noch gelesen wird, kommt zu spät: das Lied wird so angelegt.
    lesungenAbbrechen();
    sende({ type: "speichern-angefordert", entwurfId, anfrageId });
    // Solange das Lied angelegt wird, ändert sich der gespeicherte Entwurf nicht.
    sicherungGesperrt.current = true;
    await sicherungKette.current;

    let liedId: string;
    try {
      if (aktuell.ziel.status === "vorhanden") {
        // Zu einem vorhandenen Lied: es wird nichts angelegt oder geändert.
        liedId = aktuell.ziel.liedId;
      } else {
        const lied = await legeLiedAn({
          ...LEER,
          ...Object.fromEntries(
            Object.entries(aktuell.lied).map(([feld, wert]) => [
              feld,
              wert.trim(),
            ]),
          ),
        });
        liedId = lied.id;
      }
    } catch (ursache) {
      sicherungGesperrt.current = false;
      sende({
        type: "lied-anlegen-fehlgeschlagen",
        entwurfId,
        anfrageId,
        meldung: await fehlerText(ursache),
      });
      return;
    }
    sende({ type: "lied-angelegt", entwurfId, anfrageId, liedId });
    // Das Lied gibt es: der angefangene Eintrag ist erledigt.
    if (gespeichertImBackend.current) {
      gespeichertImBackend.current = false;
      void verwirfEntwurf(entwurfId).catch(() => undefined);
    }

    // Jede Datei für sich: eine gescheiterte hält die anderen nicht auf.
    for (const datei of liste) {
      if (steuerung.signal.aborted) break;
      sende({
        type: "upload-gestartet",
        entwurfId,
        dateiId: datei.id,
        versuch: 1,
      });
      if (!(await hochladen(entwurfId, liedId, datei, 1, steuerung.signal))) {
        break;
      }
    }
  }, [hochladen, lesungenAbbrechen, sende]);

  const wiederholen = useCallback(
    async (dateiId: string) => {
      const aktuell = zustandRef.current;
      if (aktuell.phase !== "speichern" && aktuell.phase !== "gespeichert") {
        return;
      }
      if (aktuell.phase === "speichern" && aktuell.liedId === null) return;
      const liedId = aktuell.liedId;
      const datei = aktuell.dateien.find((d) => d.id === dateiId);
      if (!liedId || datei?.upload.status !== "fehlgeschlagen") return;
      const versuch = datei.upload.versuch + 1;
      const { entwurfId } = aktuell;
      // Ein Wiederholen nach einem Abbruch bekommt einen frischen Abbruch.
      if (!abbruch.current || abbruch.current.signal.aborted) {
        abbruch.current = new AbortController();
      }
      sende({
        type: "wiederholen-angefordert",
        entwurfId,
        dateiId,
        versuch,
      });
      await hochladen(
        entwurfId,
        liedId,
        datei,
        versuch,
        abbruch.current.signal,
      );
    },
    [hochladen, sende],
  );

  const fehlgeschlagenEntfernen = useCallback(
    (dateiId: string) => {
      dateien.current.delete(dateiId);
      sende({
        type: "datei-entfernt",
        entwurfId: zustandRef.current.entwurfId,
        dateiId,
      });
    },
    [sende],
  );

  return {
    zustand,
    ablegen,
    liedFeldAendern,
    dateiFeldAendern,
    dateiEntfernen,
    speichern,
    wiederholen,
    lesen,
    fehlgeschlagenEntfernen,
    verwerfen,
    trefferAnnehmen,
    trefferAblehnen,
  };
}
