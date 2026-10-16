"use client";

import { useEffect, useRef, useState } from "react";
import {
  AbbruchFehler,
  groesseText,
  MaterialFehler,
  problemTitel,
} from "@/lib/assets";
import {
  gespeicherteUebertragung,
  ladeDateiHoch,
  type NeueDatei,
  setzeDateiUebertragungFort,
} from "@/lib/dateien";
import type { LiedDatei } from "@/lib/lieder";
import {
  stimmeAusName,
  stimmenFuerBesetzung,
  unterbrocheneUebertragungen,
} from "@/lib/stimmen";

type ZeilenStatus =
  | "bereit"
  | "uebertragen"
  | "geprueft"
  | "gespeichert"
  | "gescheitert"
  | "abgebrochen";

type Zeile = {
  id: number;
  datei: File;
  partitur: boolean;
  stimme: string;
  status: ZeilenStatus;
  fortschritt: number;
  fehler: string;
};

function statusText(zeile: Zeile): string {
  switch (zeile.status) {
    case "bereit":
      return "bereit";
    case "uebertragen":
      return `wird übertragen … ${Math.round(zeile.fortschritt)} %`;
    case "geprueft":
      return "wird geprüft …";
    case "gespeichert":
      return "gespeichert.";
    case "gescheitert":
      return "gescheitert";
    case "abgebrochen":
      return "abgebrochen";
  }
}

async function fehlerText(ursache: unknown): Promise<string> {
  if (ursache instanceof MaterialFehler) return ursache.message;
  return problemTitel(
    ursache,
    "Das Hochladen konnte nicht abgeschlossen werden. Bitte erneut versuchen.",
  );
}

/**
 * Noten hochladen (#166): Partitur und Stimmen-Noten (PDF), mehrere Dateien
 * auf einmal. Jede Datei geht über die fortsetzbare Übertragung; wird eine
 * Datei nach einer Unterbrechung erneut gewählt, setzt sie dort fort, wo sie
 * aufgehört hat. Weitere Dateiarten kommen mit #167.
 */
export function DateiHochladen({
  liedId,
  besetzung,
  onGespeichert,
}: {
  liedId: string;
  besetzung: string | null;
  onGespeichert: (datei: LiedDatei) => void;
}) {
  const stimmen = stimmenFuerBesetzung(besetzung);
  const eingabeRef = useRef<HTMLInputElement>(null);
  const naechsteId = useRef(0);
  const zeilenRef = useRef<Zeile[]>([]);
  const abbrueche = useRef(new Map<number, AbortController>());
  const [zeilen, setZeilen] = useState<Zeile[]>([]);
  const [laeuft, setLaeuft] = useState(false);
  const [meldung, setMeldung] = useState("");
  const [unterbrochen, setUnterbrochen] = useState<string[]>([]);

  useEffect(() => {
    setUnterbrochen(
      unterbrocheneUebertragungen(liedId).map((eintrag) => eintrag.fileName),
    );
  }, [liedId]);

  function aktualisiere(id: number, aenderung: Partial<Zeile>) {
    zeilenRef.current = zeilenRef.current.map((zeile) =>
      zeile.id === id ? { ...zeile, ...aenderung } : zeile,
    );
    setZeilen(zeilenRef.current);
  }

  function dateienGewaehlt(event: React.ChangeEvent<HTMLInputElement>) {
    const gewaehlt = Array.from(event.target.files ?? []);
    event.target.value = "";
    const neue: Zeile[] = gewaehlt.map((datei) => {
      const stimme = stimmeAusName(datei.name, stimmen);
      return {
        id: naechsteId.current++,
        datei,
        partitur: !stimme && /partitur/i.test(datei.name),
        stimme,
        status: "bereit",
        fortschritt: 0,
        fehler: "",
      };
    });
    zeilenRef.current = [...zeilenRef.current, ...neue];
    setZeilen(zeilenRef.current);
    setMeldung("");
  }

  function entfernen(id: number) {
    zeilenRef.current = zeilenRef.current.filter((zeile) => zeile.id !== id);
    setZeilen(zeilenRef.current);
  }

  async function uebertrage(zeile: Zeile) {
    const neu: NeueDatei = {
      kind: "noten",
      isPartitur: zeile.partitur,
      stimme: zeile.stimme.trim(),
    };
    const abbruch = new AbortController();
    abbrueche.current.set(zeile.id, abbruch);
    aktualisiere(zeile.id, {
      status: "uebertragen",
      fortschritt: 0,
      fehler: "",
    });
    const optionen = {
      signal: abbruch.signal,
      onFortschritt: (uebertragen: number, gesamt: number) =>
        aktualisiere(zeile.id, {
          fortschritt: gesamt === 0 ? 100 : (uebertragen / gesamt) * 100,
        }),
    };
    const schritt = (s: "uebertragen" | "geprueft") =>
      aktualisiere(zeile.id, { status: s });
    // Eine unterbrochene Übertragung derselben Datei wird fortgesetzt.
    const gemerkt = gespeicherteUebertragung(liedId, neu);
    const fortsetzbar =
      gemerkt !== null &&
      gemerkt.fileName === zeile.datei.name &&
      gemerkt.sizeBytes === zeile.datei.size &&
      gemerkt.lastModified === zeile.datei.lastModified;
    try {
      const gespeichert = fortsetzbar
        ? await setzeDateiUebertragungFort(
            liedId,
            neu,
            zeile.datei,
            schritt,
            optionen,
          )
        : await ladeDateiHoch(liedId, neu, zeile.datei, schritt, optionen);
      aktualisiere(zeile.id, { status: "gespeichert", fortschritt: 100 });
      onGespeichert(gespeichert);
    } catch (ursache) {
      aktualisiere(
        zeile.id,
        ursache instanceof AbbruchFehler
          ? { status: "abgebrochen" }
          : { status: "gescheitert", fehler: await fehlerText(ursache) },
      );
    } finally {
      abbrueche.current.delete(zeile.id);
    }
  }

  async function hochladen() {
    if (laeuft) return;
    const offen = zeilenRef.current.filter(
      (zeile) => zeile.status !== "gespeichert",
    );
    const ungueltig = offen.find(
      (zeile) => !zeile.partitur && !zeile.stimme.trim(),
    );
    if (ungueltig) {
      setMeldung(
        `Bitte für „${ungueltig.datei.name}“ eine Stimme angeben oder Partitur wählen.`,
      );
      return;
    }
    setMeldung("");
    setLaeuft(true);
    for (const zeile of offen) await uebertrage(zeile);
    setLaeuft(false);
    const gesamt = zeilenRef.current.length;
    const gesichert = zeilenRef.current.filter(
      (zeile) => zeile.status === "gespeichert",
    ).length;
    setUnterbrochen(
      unterbrocheneUebertragungen(liedId).map((eintrag) => eintrag.fileName),
    );
    setMeldung(
      gesichert === gesamt
        ? `${gesichert} von ${gesamt} Dateien gespeichert.`
        : `${gesichert} von ${gesamt} Dateien gespeichert. Gescheiterte Dateien können erneut übertragen werden.`,
    );
  }

  const offen = zeilen.some((zeile) => zeile.status !== "gespeichert");

  return (
    <section
      className="noten-bereich datei-hochladen"
      aria-label="Noten hochladen"
    >
      <h3>Noten hochladen</h3>
      <p className="noten-info">
        Partitur oder Noten einer Stimme (PDF). Mehrere Dateien auf einmal sind
        möglich.
      </p>
      {unterbrochen.length > 0 && !laeuft && (
        <output className="noten-info">
          Unterbrochene Übertragung: {unterbrochen.join(", ")}. Wähle die Datei
          erneut aus, um sie fortzusetzen.
        </output>
      )}
      <label htmlFor="datei-hochladen-auswahl">Noten auswählen</label>
      <input
        ref={eingabeRef}
        id="datei-hochladen-auswahl"
        type="file"
        accept="application/pdf,.pdf"
        multiple
        onChange={dateienGewaehlt}
      />
      <datalist id="datei-hochladen-stimmen">
        {stimmen.map((stimme) => (
          <option key={stimme} value={stimme} />
        ))}
      </datalist>
      {zeilen.length > 0 && (
        <ul className="material-upload-liste">
          {zeilen.map((zeile) => (
            <li key={zeile.id} className="material-upload-zeile">
              <p className="noten-info">
                {zeile.datei.name} · {groesseText(zeile.datei.size)} ·{" "}
                {statusText(zeile)}
              </p>
              <label>
                <input
                  type="checkbox"
                  checked={zeile.partitur}
                  disabled={zeile.status === "gespeichert" || laeuft}
                  onChange={(event) =>
                    aktualisiere(zeile.id, { partitur: event.target.checked })
                  }
                />
                Partitur
              </label>
              {!zeile.partitur && (
                <>
                  <label htmlFor={`datei-stimme-${zeile.id}`}>
                    Stimme für {zeile.datei.name}
                  </label>
                  <input
                    id={`datei-stimme-${zeile.id}`}
                    type="text"
                    list="datei-hochladen-stimmen"
                    maxLength={200}
                    value={zeile.stimme}
                    disabled={zeile.status === "gespeichert" || laeuft}
                    onChange={(event) =>
                      aktualisiere(zeile.id, { stimme: event.target.value })
                    }
                  />
                </>
              )}
              {zeile.fehler && (
                <output aria-live="polite" className="feld-fehler">
                  {zeile.fehler}
                </output>
              )}
              <div className="noten-aktionen">
                {zeile.status === "uebertragen" && (
                  <button
                    type="button"
                    className="knopf-leise"
                    onClick={() => abbrueche.current.get(zeile.id)?.abort()}
                  >
                    Abbrechen
                  </button>
                )}
                {zeile.status !== "uebertragen" &&
                  zeile.status !== "geprueft" &&
                  zeile.status !== "gespeichert" && (
                    <button
                      type="button"
                      className="knopf-leise"
                      onClick={() => entfernen(zeile.id)}
                      disabled={laeuft}
                    >
                      Entfernen
                    </button>
                  )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {meldung && (
        <output aria-live="polite" className="auth-erfolg">
          {meldung}
        </output>
      )}
      {offen && (
        <div className="auth-aktionen">
          <button
            type="button"
            onClick={() => void hochladen()}
            disabled={laeuft}
          >
            {laeuft
              ? "Wird hochgeladen …"
              : zeilen.some(
                    (zeile) =>
                      zeile.status === "gescheitert" ||
                      zeile.status === "abgebrochen",
                  )
                ? "Erneut übertragen"
                : "Hochladen"}
          </button>
        </div>
      )}
    </section>
  );
}
