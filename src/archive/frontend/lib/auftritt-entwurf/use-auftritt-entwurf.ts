"use client";

import { useCallback, useEffect, useReducer, useRef } from "react";
import {
  type Aufnahme,
  artFuerDatei,
  createAufnahme,
  fetchAufnahmen,
  messeDauer,
  patchAufnahme,
} from "@/lib/aufnahmen";
import {
  speichereAuftrittEntwurf,
  verwirfAuftrittEntwurf,
} from "@/lib/auftritt-entwuerfe";
import { LesenFehler, leseProgramm } from "@/lib/auftritt-lesen";
import {
  auftrittUploadSchluessel,
  haengeAufnahmeDateiAn,
  ladeAuftrittDateiHoch,
} from "@/lib/dateien";
import {
  contentTypeFuerDokument,
  legeAuftrittAn,
  putProgrammItems,
} from "@/lib/events";
import { legeLiedAn } from "@/lib/lieder";
import { AbbruchFehler, problemTitel } from "@/lib/uebertragung";
import { alsGespeichert } from "./gespeichert.ts";
import {
  anfangszustand,
  artAusDatei,
  type DateiArt,
  type Ereignis,
  kannSpeichern,
  type LiedRef,
  reduziere,
  type TextFeld,
  type Zustand,
} from "./reducer.ts";

// Der Ablauf um den Reducer (#131): Dateien annehmen, Programmzettel lesen,
// "Angefangen" sichern, Auftritt anlegen und die Dateien übertragen. Der
// Reducer entscheidet allein, was ein Ereignis am Zustand ändert; hier laufen
// nur Netz, Zeit und die Bytes der Dateien (die nie gespeichert werden).

/** So lange nach der letzten Änderung wird der Entwurf gesichert. */
const SICHERN_NACH_MS = 700;

const SPEICHERN_ERSATZ = "Das hat nicht geklappt. Bitte erneut versuchen.";

function neueId(): string {
  return crypto.randomUUID();
}

function hoechsteAnfrage(zustand: Zustand | undefined): number {
  return Math.max(
    0,
    ...(zustand?.dateien.map((d) =>
      d.lesen.status === "keine" ? 0 : d.lesen.anfrageId,
    ) ?? []),
  );
}

export type AuftrittEntwurf = ReturnType<typeof useAuftrittEntwurf>;

export function useAuftrittEntwurf(wiederhergestellt?: Zustand) {
  const [zustand, dispatch] = useReducer(
    reduziere,
    wiederhergestellt,
    (start) => start ?? anfangszustand(neueId()),
  );
  // Der Reducer ist rein: dieselbe Faltung hier und in React ergibt denselben
  // Zustand, und die Ablaufsteuerung sieht ihn ohne Rendern.
  const zustandRef = useRef(zustand);
  const dateien = useRef(new Map<string, File>());
  const lesungen = useRef(new Map<string, AbortController>());
  const lesAnfragen = useRef(hoechsteAnfrage(wiederhergestellt));
  const speichertGerade = useRef(false);
  const imBackend = useRef(wiederhergestellt !== undefined);
  const sicherungOffen = useRef(false);
  const sicherungGesperrt = useRef(false);
  const sicherungTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const sicherungKette = useRef<Promise<void>>(Promise.resolve());

  /** Schickt den aktuellen Entwurf ans Backend; nacheinander, der letzte Stand gilt. */
  const sichernJetzt = useCallback((schliessend = false): Promise<void> => {
    if (sicherungTimer.current !== null) clearTimeout(sicherungTimer.current);
    sicherungTimer.current = null;
    sicherungKette.current = sicherungKette.current.then(async () => {
      if (sicherungGesperrt.current) return;
      const aktuell = zustandRef.current;
      try {
        const abzug = alsGespeichert(aktuell);
        if (!abzug) {
          // Ohne Dateien (oder wenn alles oben ist) bleibt nichts Angefangenes.
          if (imBackend.current) {
            imBackend.current = false;
            await verwirfAuftrittEntwurf(aktuell.entwurfId);
          }
          sicherungOffen.current = false;
          return;
        }
        sicherungOffen.current = false;
        imBackend.current = true;
        await speichereAuftrittEntwurf(
          aktuell.entwurfId,
          aktuell.werte.title,
          abzug,
          schliessend,
        );
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
      if (nachher !== vorher && nachher.phase !== "gespeichert") {
        sicherungPlanen();
      }
    },
    [sicherungPlanen],
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
    const verlassen = () => {
      if (sicherungOffen.current) void sichernJetzt(true);
    };
    const sichtbarkeit = () => {
      if (document.visibilityState === "hidden") verlassen();
    };
    window.addEventListener("pagehide", verlassen);
    document.addEventListener("visibilitychange", sichtbarkeit);
    return () => {
      window.removeEventListener("pagehide", verlassen);
      document.removeEventListener("visibilitychange", sichtbarkeit);
      verlassen();
      lesungenAbbrechen();
    };
  }, [lesungenAbbrechen, sichernJetzt]);

  const lesen = useCallback(
    async (dateiId: string) => {
      const datei = dateien.current.get(dateiId);
      if (!datei) return;
      lesungen.current.get(dateiId)?.abort();
      const steuerung = new AbortController();
      lesungen.current.set(dateiId, steuerung);
      const anfrageId = ++lesAnfragen.current;
      sende({ type: "lesen-beginnt", dateiId, anfrageId });
      try {
        const lesung = await leseProgramm(
          zustandRef.current.entwurfId,
          anfrageId,
          datei,
          steuerung.signal,
        );
        if (steuerung.signal.aborted) return;
        sende({
          type: "lesen-fertig",
          dateiId,
          anfrageId,
          felder: lesung.felder,
          programm: lesung.programm,
        });
      } catch (ursache) {
        if (steuerung.signal.aborted || ursache instanceof AbbruchFehler)
          return;
        sende({
          type: "lesen-gescheitert",
          dateiId,
          anfrageId,
          meldung:
            ursache instanceof LesenFehler
              ? ursache.message
              : "Das Lesen hat nicht geklappt. Bitte erneut versuchen.",
          verfuegbar:
            ursache instanceof LesenFehler ? ursache.verfuegbar : true,
        });
      } finally {
        if (lesungen.current.get(dateiId) === steuerung)
          lesungen.current.delete(dateiId);
      }
    },
    [sende],
  );

  const ablegen = useCallback(
    (dateiListe: File[]) => {
      const neu: { id: string; datei: File; art: DateiArt }[] = [];
      for (const datei of dateiListe) {
        // Eine wiederhergestellte Datei ohne Inhalt: gleicher Name und gleiche Größe.
        const fehlend = zustandRef.current.dateien.find(
          (d) =>
            d.inhalt === "fehlt" &&
            d.name === datei.name &&
            d.sizeBytes === datei.size,
        );
        if (fehlend) {
          dateien.current.set(fehlend.id, datei);
          sende({ type: "datei-inhalt-da", dateiId: fehlend.id });
          if (fehlend.art === "programm" && fehlend.lesen.status !== "gelesen")
            void lesen(fehlend.id);
          continue;
        }
        neu.push({
          id: neueId(),
          datei,
          art: artAusDatei(datei.name, datei.type),
        });
      }
      if (neu.length === 0) return;
      for (const eintrag of neu) dateien.current.set(eintrag.id, eintrag.datei);
      sende({
        type: "dateien-abgelegt",
        dateien: neu.map((e) => ({
          id: e.id,
          name: e.datei.name,
          sizeBytes: e.datei.size,
          art: e.art,
        })),
      });
      for (const eintrag of neu) {
        if (eintrag.art === "programm") void lesen(eintrag.id);
      }
    },
    [lesen, sende],
  );

  const dateiEntfernen = useCallback(
    (dateiId: string) => {
      lesungenAbbrechen(dateiId);
      dateien.current.delete(dateiId);
      sende({ type: "datei-entfernt", dateiId });
    },
    [lesungenAbbrechen, sende],
  );

  const dateiArtAendern = useCallback(
    (dateiId: string, art: DateiArt) => {
      sende({ type: "datei-art", dateiId, art });
    },
    [sende],
  );

  const feldAendern = useCallback(
    (feld: TextFeld, wert: string) =>
      sende({ type: "feld-geaendert", feld, wert }),
    [sende],
  );

  const unsicherAendern = useCallback(
    (wert: boolean) => sende({ type: "unsicher-geaendert", wert }),
    [sende],
  );

  const zeileWaehlen = useCallback(
    (zeileId: string, lied: LiedRef) =>
      sende({ type: "zeile-gewaehlt", zeileId, lied }),
    [sende],
  );

  const zeileAnnehmen = useCallback(
    (zeileId: string) => sende({ type: "zeile-angenommen", zeileId }),
    [sende],
  );

  const zeileEntfernen = useCallback(
    (zeileId: string) => sende({ type: "zeile-entfernt", zeileId }),
    [sende],
  );

  const zeileHinzufuegen = useCallback(
    (lied: LiedRef) =>
      sende({ type: "zeile-hinzugefuegt", zeileId: neueId(), lied }),
    [sende],
  );

  const zeileVerschieben = useCallback(
    (zeileId: string, richtung: -1 | 1) =>
      sende({ type: "zeile-verschoben", zeileId, richtung }),
    [sende],
  );

  /** Legt zum Text der Zeile ein neues Lied an (nur der Titel) und wählt es. */
  const neuesLiedAnlegen = useCallback(
    async (zeileId: string): Promise<string | null> => {
      const zeile = zustandRef.current.programm.find((z) => z.id === zeileId);
      if (!zeile) return null;
      try {
        const lied = await legeLiedAn({
          title: zeile.text,
          composer: "",
          arranger: "",
          lyricist: "",
          voiceConfiguration: "",
          musicalKey: "",
          language: "",
          lyrics: "",
          occasion: "",
        });
        sende({
          type: "zeile-gewaehlt",
          zeileId,
          lied: { id: lied.id, titel: lied.title },
        });
        return null;
      } catch (ursache) {
        return problemTitel(ursache, "Das Lied konnte nicht angelegt werden.");
      }
    },
    [sende],
  );

  const hochladen = useCallback(
    async (auftrittId: string, dateiId: string): Promise<void> => {
      const eintrag = zustandRef.current.dateien.find((d) => d.id === dateiId);
      const datei = dateien.current.get(dateiId);
      if (!eintrag || eintrag.upload.status === "hochgeladen") return;
      if (!datei || eintrag.inhalt !== "vorhanden") {
        sende({
          type: "upload",
          dateiId,
          upload: {
            status: "fehlgeschlagen",
            meldung: `Die Datei liegt nicht mehr im Browser. Bitte ${eintrag.name} erneut ablegen.`,
          },
        });
        return;
      }
      sende({
        type: "upload",
        dateiId,
        upload: { status: "laedt", bytes: 0, gesamt: datei.size },
      });
      const optionen = {
        onFortschritt: (bytes: number, gesamt: number) =>
          sende({
            type: "upload",
            dateiId,
            upload: { status: "laedt", bytes, gesamt },
          }),
      };
      const schluessel = auftrittUploadSchluessel(
        auftrittId,
        `entwurf-${dateiId}`,
      );
      try {
        if (eintrag.art === "aufnahme") {
          const art = artFuerDatei(datei);
          const aufnahme = await aufnahmeFuer(auftrittId, eintrag.name, art);
          const hochgeladen = await ladeAuftrittDateiHoch(
            auftrittId,
            schluessel,
            {
              kind: "aufnahme",
              contentType: /^(audio|video)\//.test(datei.type)
                ? datei.type
                : `${art}/mp4`,
            },
            datei,
            undefined,
            optionen,
          );
          await haengeAufnahmeDateiAn(aufnahme.id, "original", hochgeladen.id);
          // Die Länge misst der Browser aus der lokalen Datei; ohne sie bleibt sie unbekannt.
          const dauer = await messeDauer(datei, art);
          if (dauer !== null)
            await patchAufnahme(aufnahme.id, { durationSeconds: dauer }).catch(
              () => {},
            );
        } else {
          await ladeAuftrittDateiHoch(
            auftrittId,
            schluessel,
            { kind: "sonstiges", contentType: contentTypeFuerDokument(datei) },
            datei,
            undefined,
            optionen,
          );
        }
        sende({ type: "upload", dateiId, upload: { status: "hochgeladen" } });
      } catch (ursache) {
        sende({
          type: "upload",
          dateiId,
          upload: {
            status: "fehlgeschlagen",
            meldung: await problemTitel(
              ursache,
              "Die Übertragung ist gescheitert. Bitte erneut versuchen.",
            ),
          },
        });
      }
    },
    [sende],
  );

  const speichern = useCallback(async () => {
    const start = zustandRef.current;
    if (speichertGerade.current) return;
    if (
      start.phase === "sammeln"
        ? !kannSpeichern(start)
        : start.phase !== "speichern"
    )
      return;
    speichertGerade.current = true;
    sende({ type: "speichern-beginnt" });
    try {
      let auftrittId = zustandRef.current.auftrittId;
      if (!auftrittId) {
        const angelegt = await legeAuftrittAn(zustandRef.current.werte);
        auftrittId = angelegt.id;
        sende({ type: "auftritt-angelegt", auftrittId });
        // Der Entwurf merkt sich den Auftritt sofort: ein Neuladen legt ihn nicht doppelt an.
        await sichernJetzt();
      }
      if (!zustandRef.current.programmGespeichert) {
        const items = zustandRef.current.programm.flatMap((z) =>
          z.lied ? [{ liedId: z.lied.id }] : [],
        );
        if (items.length > 0) await putProgrammItems(auftrittId, { items });
        sende({ type: "programm-gespeichert" });
      }
      for (const datei of zustandRef.current.dateien) {
        await hochladen(auftrittId, datei.id);
      }
      const offen = zustandRef.current.dateien.filter(
        (d) => d.upload.status !== "hochgeladen",
      );
      if (offen.length > 0) {
        sende({
          type: "speichern-gescheitert",
          meldung:
            offen.length === 1
              ? "Eine Datei konnte nicht übertragen werden. Der Auftritt ist angelegt."
              : `${offen.length} Dateien konnten nicht übertragen werden. Der Auftritt ist angelegt.`,
        });
        await sichernJetzt();
        return;
      }
      sende({ type: "gespeichert" });
      // Der Entwurf endet mit dem Auftritt.
      await sichernJetzt();
    } catch (ursache) {
      sende({
        type: "speichern-gescheitert",
        meldung: await problemTitel(ursache, SPEICHERN_ERSATZ),
      });
      await sichernJetzt();
    } finally {
      speichertGerade.current = false;
    }
  }, [hochladen, sende, sichernJetzt]);

  const verwerfen = useCallback(async () => {
    sicherungGesperrt.current = true;
    if (sicherungTimer.current !== null) clearTimeout(sicherungTimer.current);
    lesungenAbbrechen();
    try {
      await sicherungKette.current;
      if (imBackend.current) {
        imBackend.current = false;
        await verwirfAuftrittEntwurf(zustandRef.current.entwurfId);
      }
    } finally {
      dateien.current.clear();
    }
  }, [lesungenAbbrechen]);

  return {
    zustand,
    ablegen,
    lesen,
    dateiEntfernen,
    dateiArtAendern,
    feldAendern,
    unsicherAendern,
    zeileWaehlen,
    zeileAnnehmen,
    zeileEntfernen,
    zeileHinzufuegen,
    zeileVerschieben,
    neuesLiedAnlegen,
    speichern,
    verwerfen,
  };
}

/**
 * Die Aufnahme zum Video: eine vorhandene leere desselben Namens (nach einem
 * abgebrochenen Versuch), sonst eine neue.
 */
async function aufnahmeFuer(
  auftrittId: string,
  dateiname: string,
  art: "audio" | "video",
): Promise<Aufnahme> {
  const label = dateiname.replace(/\.[^.]+$/, "") || dateiname;
  const vorhandene = await fetchAufnahmen(auftrittId).catch(() => []);
  const leer = vorhandene.find(
    (a) => a.label === label && a.editor?.original.file === null,
  );
  return leer ?? (await createAufnahme(auftrittId, { label, kind: art }));
}
