"use client";

import { useCallback, useEffect, useReducer, useRef } from "react";
import { inhaltstypFuerDatei, ladeDateiHoch } from "@/lib/dateien";
import { type LiedEingabe, legeLiedAn } from "@/lib/lieder";
import {
  AbbruchFehler,
  MaterialFehler,
  problemTitel,
} from "@/lib/uebertragung";
import {
  anfangszustand,
  type DateiEntwurf,
  type DateiFeld,
  type DateiTyp,
  kannSpeichern,
  type LiedFeld,
  reduziere,
  type Zustand,
} from "./reducer";

// Der Hook führt aus, was der Reducer nicht darf: das Lied anlegen, die Dateien
// hochladen, abbrechen. Jedes Ergebnis geht mit Entwurf, Anfrage oder Versuch
// zurück an den Reducer, der über Veraltetes selbst entscheidet.

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
  if (ursache instanceof MaterialFehler) return ursache.message;
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

export function useLiedEntwurf() {
  const [zustand, dispatch] = useReducer(
    reduziere,
    undefined,
    (): Zustand => anfangszustand(neueId()),
  );
  const zustandRef = useRef(zustand);
  zustandRef.current = zustand;
  const dateien = useRef(new Map<string, File>());
  const anfragen = useRef(0);
  const abbruch = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      abbruch.current?.abort();
    },
    [],
  );

  const ablegen = useCallback((gewaehlte: File[]) => {
    const neue = gewaehlte.map((datei) => {
      const id = neueId();
      dateien.current.set(id, datei);
      return { id, name: datei.name, sizeBytes: datei.size };
    });
    dispatch({
      type: "dateien-abgelegt",
      entwurfId: zustandRef.current.entwurfId,
      dateien: neue,
    });
  }, []);

  const liedFeldAendern = useCallback((feld: LiedFeld, wert: string) => {
    dispatch({
      type: "lied-feld-geaendert",
      entwurfId: zustandRef.current.entwurfId,
      feld,
      wert,
    });
  }, []);

  const dateiFeldAendern = useCallback(
    (dateiId: string, feld: DateiFeld, wert: string) => {
      const entwurfId = zustandRef.current.entwurfId;
      dispatch(
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
    [],
  );

  const dateiEntfernen = useCallback((dateiId: string) => {
    dateien.current.delete(dateiId);
    dispatch({
      type: "datei-entfernt",
      entwurfId: zustandRef.current.entwurfId,
      dateiId,
    });
  }, []);

  const verwerfen = useCallback(() => {
    abbruch.current?.abort();
    dispatch({
      type: "entwurf-verworfen",
      entwurfId: zustandRef.current.entwurfId,
    });
  }, []);

  const speichern = useCallback(async () => {
    const aktuell = zustandRef.current;
    if (aktuell.phase !== "sammeln" || !kannSpeichern(aktuell)) return;
    const { entwurfId } = aktuell;
    const anfrageId = ++anfragen.current;
    const steuerung = new AbortController();
    abbruch.current = steuerung;
    const liste = aktuell.dateien;
    dispatch({ type: "speichern-angefordert", entwurfId, anfrageId });

    let liedId: string;
    try {
      const lied = await legeLiedAn({
        ...LEER,
        title: aktuell.lied.title.trim(),
      });
      liedId = lied.id;
    } catch (ursache) {
      dispatch({
        type: "lied-anlegen-fehlgeschlagen",
        entwurfId,
        anfrageId,
        meldung: await fehlerText(ursache),
      });
      return;
    }
    dispatch({ type: "lied-angelegt", entwurfId, anfrageId, liedId });

    // Jede Datei für sich: eine gescheiterte hält die anderen nicht auf.
    for (const datei of liste) {
      const original = dateien.current.get(datei.id);
      if (!original || steuerung.signal.aborted) break;
      const versuch = 1;
      dispatch({
        type: "upload-gestartet",
        entwurfId,
        dateiId: datei.id,
        versuch,
      });
      try {
        const gespeichert = await ladeDateiHoch(
          liedId,
          hochladeArt(datei, original),
          original,
          undefined,
          {
            signal: steuerung.signal,
            onFortschritt: (bytes, gesamt) =>
              dispatch({
                type: "upload-fortschritt",
                entwurfId,
                dateiId: datei.id,
                versuch,
                bytes,
                gesamt,
              }),
          },
        );
        dispatch({
          type: "upload-erfolgreich",
          entwurfId,
          dateiId: datei.id,
          versuch,
          serverDateiId: gespeichert.id,
        });
      } catch (ursache) {
        if (ursache instanceof AbbruchFehler) break;
        dispatch({
          type: "upload-fehlgeschlagen",
          entwurfId,
          dateiId: datei.id,
          versuch,
          meldung: await fehlerText(ursache),
        });
      }
    }
  }, []);

  return {
    zustand,
    ablegen,
    liedFeldAendern,
    dateiFeldAendern,
    dateiEntfernen,
    speichern,
    verwerfen,
  };
}
