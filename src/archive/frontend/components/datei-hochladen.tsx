"use client";

import { useEffect, useRef, useState } from "react";
import {
  gespeicherteUebertragung,
  inhaltstypFuerDatei,
  ladeDateiHoch,
  type NeueDatei,
  setzeDateiUebertragungFort,
} from "@/lib/dateien";
import { fetchEvents } from "@/lib/events";
import { type DateiArt, dateiArten, type LiedDatei } from "@/lib/lieder";
import {
  stimmeAusName,
  stimmenFuerBesetzung,
  unterbrocheneUebertragungen,
} from "@/lib/stimmen";
import {
  AbbruchFehler,
  groesseText,
  MaterialFehler,
  problemTitel,
} from "@/lib/uebertragung";

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

type ArtBeschreibung = {
  titel: string;
  hinweis: string;
  accept: string;
  stimme: "pflicht" | "keine";
};

const artBeschreibung: Record<DateiArt, ArtBeschreibung> = {
  noten: {
    titel: "Noten",
    hinweis:
      "Partitur oder Noten einer Stimme (PDF). Mehrere Dateien auf einmal sind möglich.",
    accept: "application/pdf,.pdf",
    stimme: "pflicht",
  },
  uebe: {
    titel: "Übe-Dateien",
    hinweis:
      "Ton (MP3, M4A, WAV, OGG) oder MIDI zum Üben, für eine Stimme oder „Alle“.",
    accept: "audio/*,.mp3,.m4a,.wav,.ogg,.mid,.midi",
    stimme: "pflicht",
  },
  aufnahme: {
    titel: "Aufnahmen",
    hinweis:
      "Ton oder Video des Chores bei diesem Lied, auf Wunsch mit dem Auftritt.",
    accept: "audio/*,video/mp4,video/webm,.mp3,.m4a,.wav,.ogg,.mp4,.webm",
    stimme: "keine",
  },
  sonstiges: {
    titel: "Sonstiges",
    hinweis: "PDF, Bild, Text oder Office-Dokument zum Herunterladen.",
    accept: "application/pdf,image/*,text/plain,.pdf,.txt,.docx,.xlsx,.pptx",
    stimme: "keine",
  },
};

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
  const [art, setArt] = useState<DateiArt>("noten");
  const [auftritte, setAuftritte] = useState<{ id: string; titel: string }[]>(
    [],
  );
  const [auftrittId, setAuftrittId] = useState("");
  const beschreibung = artBeschreibung[art];
  const stimmePflicht = beschreibung.stimme === "pflicht";

  useEffect(() => {
    setUnterbrochen(
      unterbrocheneUebertragungen(liedId).map((eintrag) => eintrag.fileName),
    );
  }, [liedId]);

  useEffect(() => {
    if (art !== "aufnahme" || auftritte.length > 0) return;
    const abbruch = new AbortController();
    fetchEvents({}, abbruch.signal)
      .then((ergebnis) =>
        setAuftritte(
          ergebnis.events.map((auftritt) => ({
            id: auftritt.id,
            titel: auftritt.dateDisplay
              ? `${auftritt.title} (${auftritt.dateDisplay})`
              : auftritt.title,
          })),
        ),
      )
      .catch(() => {});
    return () => abbruch.abort();
  }, [art, auftritte.length]);

  function artWechseln(neueArt: DateiArt) {
    if (zeilenRef.current.some((zeile) => zeile.status !== "gespeichert")) {
      setMeldung("Bitte erst die gewählten Dateien hochladen oder entfernen.");
      return;
    }
    zeilenRef.current = [];
    setZeilen([]);
    setMeldung("");
    setArt(neueArt);
  }

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
      const stimme = stimmePflicht ? stimmeAusName(datei.name, stimmen) : "";
      return {
        id: naechsteId.current++,
        datei,
        partitur: art === "noten" && !stimme && /partitur/i.test(datei.name),
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
      kind: art,
      isPartitur: zeile.partitur,
      stimme: stimmePflicht ? zeile.stimme.trim() : "",
      contentType:
        art === "noten" ? "application/pdf" : inhaltstypFuerDatei(zeile.datei),
      dateiName: zeile.datei.name,
      recordedAtEventId: art === "aufnahme" ? auftrittId || null : null,
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
      (zeile) => stimmePflicht && !zeile.partitur && !zeile.stimme.trim(),
    );
    if (ungueltig) {
      setMeldung(
        art === "noten"
          ? `Bitte für „${ungueltig.datei.name}“ eine Stimme angeben oder Partitur wählen.`
          : `Bitte für „${ungueltig.datei.name}“ eine Stimme angeben oder „Alle“ wählen.`,
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
      aria-label={`${beschreibung.titel} hochladen`}
    >
      <h3>{beschreibung.titel} hochladen</h3>
      <label htmlFor="datei-hochladen-art">Art der Datei</label>
      <select
        id="datei-hochladen-art"
        value={art}
        disabled={laeuft}
        onChange={(event) => artWechseln(event.target.value as DateiArt)}
      >
        {dateiArten.map(([wert, titel]) => (
          <option key={wert} value={wert}>
            {titel}
          </option>
        ))}
      </select>
      <p className="noten-info">{beschreibung.hinweis}</p>
      {art === "aufnahme" && (
        <>
          <label htmlFor="datei-hochladen-auftritt">Auftritt (optional)</label>
          <select
            id="datei-hochladen-auftritt"
            value={auftrittId}
            disabled={laeuft}
            onChange={(event) => setAuftrittId(event.target.value)}
          >
            <option value="">Kein Auftritt</option>
            {auftritte.map((auftritt) => (
              <option key={auftritt.id} value={auftritt.id}>
                {auftritt.titel}
              </option>
            ))}
          </select>
        </>
      )}
      {unterbrochen.length > 0 && !laeuft && (
        <output className="noten-info">
          Unterbrochene Übertragung: {unterbrochen.join(", ")}. Wähle die Datei
          erneut aus, um sie fortzusetzen.
        </output>
      )}
      <label htmlFor="datei-hochladen-auswahl">
        {beschreibung.titel} auswählen
      </label>
      <input
        ref={eingabeRef}
        id="datei-hochladen-auswahl"
        type="file"
        accept={beschreibung.accept}
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
              {art === "noten" && (
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
              )}
              {stimmePflicht && !zeile.partitur && (
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
