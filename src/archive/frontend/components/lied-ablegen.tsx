"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { EntwurfFehler, ladeEntwurf } from "@/lib/lied-entwuerfe";
import type { Gespeichert } from "@/lib/lied-entwurf/gespeichert";
import {
  type DateiEntwurf,
  fehlgeschlagene,
  kannSpeichern,
  type LiedFeld,
  liedFormularFelder,
  type Zustand,
} from "@/lib/lied-entwurf/reducer";
import {
  type FestesLied,
  useLiedEntwurf,
} from "@/lib/lied-entwurf/use-lied-entwurf";
import { liedFeldBeschriftung } from "@/lib/lieder";
import { groesseText } from "@/lib/uebertragung";

const dateiTypen: [string, string][] = [
  ["partitur", "Partitur"],
  ["noten", "Noten"],
  ["uebe", "Übe-Datei"],
  ["aufnahme", "Aufnahme"],
  ["sonstiges", "Sonstiges"],
];

function lesenText(datei: DateiEntwurf): string | null {
  switch (datei.lesen.status) {
    case "keine":
      return null;
    case "liest":
      return "Wird gelesen …";
    case "gelesen":
      return "Gelesen";
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      return datei.lesen.meldung;
  }
}

/** Hinweis hinter einem Feld, das ein Lesen gefüllt hat und das noch niemand angefasst hat. */
function VorschlagMarke({
  zustand,
  feld,
}: {
  zustand: Exclude<Zustand, { phase: "leer" | "verworfen" }>;
  feld: LiedFeld;
}) {
  const sicherheit = zustand.vorschlaege[feld];
  if (!sicherheit) return null;
  return (
    <span className="feld-hinweis" data-vorschlag={sicherheit}>
      {sicherheit === "unsicher" ? "Vorschlag, unsicher" : "Vorschlag"}
    </span>
  );
}

function uploadText(datei: DateiEntwurf): string {
  switch (datei.upload.status) {
    case "wartend":
      return "Bereit";
    case "laedt":
      return "Wird hochgeladen …";
    case "hochgeladen":
      return "Hochgeladen";
    case "fehlgeschlagen":
      return `Fehlgeschlagen: ${datei.upload.meldung}`;
  }
}

/**
 * "Neues Lied" (#175): Dateien ablegen, das Formular füllt sich aus den Namen,
 * ein Klick auf "Speichern" legt das Lied mit seinen Dateien an. Ohne Datei
 * geht es über den kleinen Link zum Formular von Hand.
 */
export function LiedAblegen({ entwurfId }: { entwurfId?: string | null }) {
  const [vonHand, setVonHand] = useState(false);
  const router = useRouter();
  // Ein neuer Entwurf nach dem Verwerfen bekommt einen frischen Hook.
  const [lauf, setLauf] = useState(0);
  const [fortsetzen, setFortsetzen] = useState<
    | { status: "laedt" }
    | { status: "bereit"; entwurf: Gespeichert }
    | { status: "fehler"; meldung: string }
    | null
  >(entwurfId ? { status: "laedt" } : null);

  useEffect(() => {
    if (!entwurfId) {
      setFortsetzen(null);
      return;
    }
    const steuerung = new AbortController();
    setFortsetzen({ status: "laedt" });
    ladeEntwurf(entwurfId, steuerung.signal).then(
      (entwurf) => setFortsetzen({ status: "bereit", entwurf }),
      (ursache) => {
        if (steuerung.signal.aborted) return;
        setFortsetzen({
          status: "fehler",
          meldung:
            ursache instanceof EntwurfFehler
              ? ursache.message
              : "Der Eintrag konnte nicht geladen werden.",
        });
      },
    );
    return () => steuerung.abort();
  }, [entwurfId]);

  if (vonHand) {
    return (
      <>
        <LiedFormular
          absendenText="Lied anlegen"
          onSuccess={(angelegt) => {
            // Nach dem Anlegen geht es zum Lied, um Dateien hinzuzufügen.
            router.push(`/lied/?id=${encodeURIComponent(angelegt.id)}`);
          }}
        />
        <p className="feld-hinweis">
          <button
            type="button"
            className="knopf-leise"
            onClick={() => setVonHand(false)}
          >
            Stattdessen Dateien ablegen
          </button>
        </p>
      </>
    );
  }
  if (fortsetzen?.status === "laedt") {
    return (
      <p aria-live="polite" className="feld-hinweis">
        Angefangenes Lied wird geladen …
      </p>
    );
  }
  return (
    <>
      {fortsetzen?.status === "fehler" && (
        <p role="alert" className="feld-fehler">
          {fortsetzen.meldung}
        </p>
      )}
      <DateienAblegen
        key={`${entwurfId ?? "neu"}-${lauf}`}
        gespeichert={
          fortsetzen?.status === "bereit" && lauf === 0
            ? fortsetzen.entwurf
            : undefined
        }
        onOhneDatei={() => setVonHand(true)}
        onVerworfen={() => {
          setLauf((n) => n + 1);
          if (entwurfId) router.replace("/lieder/");
        }}
      />
    </>
  );
}

/**
 * Die Ablage und das Formular dazu. Mit `festesLied` (Lied-Seite, #179) fügt
 * sie Dateien genau diesem Lied hinzu: derselbe Ablauf, aber ohne Lied-Felder,
 * ohne Lesen und ohne angefangenen Entwurf.
 *
 * Der Sammel-Import (#143) hängt sich hier an: `wiederablage` reicht
 * wiederhergestellte Dateien nach, `ohneWeiterleitung` meldet das Ende über
 * `onFertig` statt zur Lied-Seite weiterzuleiten, und `sammelAnmeldung`
 * meldet den laufenden Zustand samt Speichern hoch, damit die Redaktion
 * mehrere klare Vorschläge auf einmal speichern kann.
 */
export function DateienAblegen({
  gespeichert,
  festesLied,
  onOhneDatei,
  onVerworfen,
  onFertig,
  wiederablage,
  ohneWeiterleitung,
  sammelAnmeldung,
}: {
  gespeichert?: Gespeichert;
  festesLied?: FestesLied;
  onOhneDatei?: () => void;
  onVerworfen?: () => void;
  onFertig?: () => void;
  /** Datei-Bytes, die einem wiederhergestellten Entwurf nachgereicht werden. */
  wiederablage?: File[];
  /** Nur melden, nicht zur Lied-Seite gehen. */
  ohneWeiterleitung?: boolean;
  sammelAnmeldung?: (meldung: {
    entwurfId: string;
    zustand: Exclude<Zustand, { phase: "leer" | "verworfen" }>;
    speichern: () => Promise<void>;
  }) => void;
}) {
  const router = useRouter();
  const idPraefix = useId();
  const [darueber, setDarueber] = useState(false);
  const {
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
  } = useLiedEntwurf(gespeichert, festesLied);

  // Die nachgereichten Bytes laufen genau einmal durch die Ablage: dieselbe
  // Bewertung wie beim Ablegen, zurück im Reducer.
  const nachgereicht = useRef<File[] | null>(null);
  useEffect(() => {
    if (!wiederablage || nachgereicht.current === wiederablage) return;
    nachgereicht.current = wiederablage;
    ablegen(wiederablage);
  }, [wiederablage, ablegen]);

  // Der Sammel-Import sieht den Zustand, um klare Vorschläge auf einmal zu
  // speichern; gemeldet wird, was sich ändert.
  const meldungRef = useRef(sammelAnmeldung);
  meldungRef.current = sammelAnmeldung;
  useEffect(() => {
    if (
      !meldungRef.current ||
      zustand.phase === "leer" ||
      zustand.phase === "verworfen"
    )
      return;
    meldungRef.current({
      entwurfId: zustand.entwurfId,
      zustand,
      speichern,
    });
  }, [zustand, speichern]);

  const gespeichertId =
    zustand.phase === "gespeichert" && fehlgeschlagene(zustand).length === 0
      ? zustand.liedId
      : null;
  useEffect(() => {
    if (!gespeichertId) return;
    if (festesLied || ohneWeiterleitung) {
      onFertig?.();
      return;
    }
    router.push(`/lied/?id=${encodeURIComponent(gespeichertId)}`);
  }, [gespeichertId, router, festesLied, ohneWeiterleitung, onFertig]);

  const ziel =
    zustand.phase === "leer" || zustand.phase === "verworfen"
      ? null
      : zustand.ziel;
  const liedFelderSichtbar = ziel?.status !== "vorhanden";

  const bearbeitbar = zustand.phase === "sammeln";
  const dateien =
    zustand.phase === "leer" || zustand.phase === "verworfen"
      ? []
      : zustand.dateien;

  return (
    <div className="lied-ablegen">
      {(zustand.phase === "leer" || zustand.phase === "sammeln") && (
        <label
          className="lied-ablegen-zone"
          data-darueber={darueber ? "ja" : undefined}
          onDragOver={(event) => {
            event.preventDefault();
            setDarueber(true);
          }}
          onDragLeave={() => setDarueber(false)}
          onDrop={(event) => {
            event.preventDefault();
            setDarueber(false);
            ablegen(Array.from(event.dataTransfer.files));
          }}
        >
          <span>Dateien hier ablegen oder auswählen</span>
          <span className="feld-hinweis">
            Noten (PDF), Übe-Dateien (MP3, WAV, MIDI), Aufnahmen und mehr.
          </span>
          <input
            type="file"
            multiple
            aria-label={
              festesLied ? "Dateien zum Lied auswählen" : "Dateien auswählen"
            }
            onChange={(event) => {
              ablegen(Array.from(event.target.files ?? []));
              event.target.value = "";
            }}
          />
        </label>
      )}

      {zustand.phase !== "leer" && zustand.phase !== "verworfen" && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void speichern();
          }}
          noValidate
        >
          {ziel?.status === "angeboten" && bearbeitbar && (
            <section
              className="lied-ablegen-treffer"
              aria-label="Vorhandenes Lied"
            >
              <p>Zu ‚{ziel.titel}‘ hinzufügen?</p>
              <div className="auth-aktionen">
                <button type="button" onClick={trefferAnnehmen}>
                  Ja, hinzufügen
                </button>
                <button
                  type="button"
                  className="knopf-leise"
                  onClick={trefferAblehnen}
                >
                  Nein, neues Lied anlegen
                </button>
              </div>
            </section>
          )}
          {ziel?.status === "vorhanden" && (
            <p className="feld-hinweis">
              Die Dateien kommen zu ‚{ziel.titel}‘.
              {!ziel.fest && bearbeitbar && (
                <>
                  {" "}
                  <button
                    type="button"
                    className="knopf-leise"
                    onClick={trefferAblehnen}
                  >
                    Stattdessen neues Lied anlegen
                  </button>
                </>
              )}
            </p>
          )}
          {zustand.ki.status === "nicht-verfuegbar" && (
            <p className="feld-hinweis" data-ki="nicht-verfuegbar">
              Die KI steht nicht zur Verfügung: {zustand.ki.meldung} Bitte die
              Felder von Hand ausfüllen; Speichern geht trotzdem.
            </p>
          )}
          {liedFelderSichtbar && (
            <>
              <label htmlFor={`${idPraefix}-titel`}>Titel</label>
              <input
                id={`${idPraefix}-titel`}
                type="text"
                maxLength={200}
                value={zustand.lied.title}
                disabled={!bearbeitbar}
                onChange={(event) =>
                  liedFeldAendern("title", event.target.value)
                }
              />
              <VorschlagMarke zustand={zustand} feld="title" />
            </>
          )}
          {liedFelderSichtbar &&
            liedFormularFelder.map((feld) => (
              <div key={feld}>
                <label htmlFor={`${idPraefix}-${feld}`}>
                  {liedFeldBeschriftung[feld][1]}
                </label>
                {feld === "lyrics" ? (
                  <textarea
                    id={`${idPraefix}-${feld}`}
                    rows={4}
                    maxLength={5000}
                    value={zustand.lied[feld]}
                    disabled={!bearbeitbar}
                    onChange={(event) =>
                      liedFeldAendern(feld, event.target.value)
                    }
                  />
                ) : (
                  <input
                    id={`${idPraefix}-${feld}`}
                    type="text"
                    maxLength={200}
                    value={zustand.lied[feld]}
                    disabled={!bearbeitbar}
                    onChange={(event) =>
                      liedFeldAendern(feld, event.target.value)
                    }
                  />
                )}
                <VorschlagMarke zustand={zustand} feld={feld} />
              </div>
            ))}
          <ul className="lied-ablegen-dateien">
            {dateien.map((datei) => (
              <li key={datei.id} data-upload={datei.upload.status}>
                <p className="lied-ablegen-name">
                  {datei.name}{" "}
                  <span className="feld-hinweis">
                    ({groesseText(datei.sizeBytes)})
                  </span>
                </p>
                <label htmlFor={`${idPraefix}-${datei.id}-art`}>
                  Art von {datei.name}
                </label>
                <select
                  id={`${idPraefix}-${datei.id}-art`}
                  value={datei.art}
                  disabled={!bearbeitbar}
                  onChange={(event) =>
                    dateiFeldAendern(datei.id, "art", event.target.value)
                  }
                >
                  {dateiTypen.map(([wert, text]) => (
                    <option key={wert} value={wert}>
                      {text}
                    </option>
                  ))}
                </select>
                {datei.art !== "partitur" && (
                  <>
                    <label htmlFor={`${idPraefix}-${datei.id}-stimme`}>
                      Stimme von {datei.name}
                    </label>
                    <input
                      id={`${idPraefix}-${datei.id}-stimme`}
                      type="text"
                      maxLength={200}
                      value={datei.stimme}
                      disabled={!bearbeitbar}
                      onChange={(event) =>
                        dateiFeldAendern(datei.id, "stimme", event.target.value)
                      }
                    />
                  </>
                )}
                {datei.inhalt === "fehlt" && bearbeitbar && (
                  <p className="feld-hinweis">
                    Die Datei liegt nicht mehr im Browser. Bitte {datei.name}{" "}
                    erneut ablegen oder auswählen, um das Lied zu speichern.
                  </p>
                )}
                {datei.upload.status === "laedt" && datei.upload.gesamt > 0 && (
                  <div className="material-fortschritt" aria-hidden="true">
                    <span
                      style={{
                        width: `${Math.round((datei.upload.bytes / datei.upload.gesamt) * 100)}%`,
                      }}
                    />
                  </div>
                )}
                {lesenText(datei) && (
                  <p
                    className={
                      datei.lesen.status === "fehlgeschlagen"
                        ? "feld-fehler"
                        : "feld-hinweis"
                    }
                    role={
                      datei.lesen.status === "fehlgeschlagen"
                        ? "alert"
                        : undefined
                    }
                  >
                    {lesenText(datei)}
                  </p>
                )}
                {datei.lesen.status === "fehlgeschlagen" &&
                  bearbeitbar &&
                  datei.inhalt === "vorhanden" && (
                    <button
                      type="button"
                      className="knopf-leise"
                      onClick={() => void lesen(datei.id)}
                    >
                      {datei.name} erneut lesen
                    </button>
                  )}
                {zustand.phase !== "sammeln" && (
                  <p
                    className={
                      datei.upload.status === "fehlgeschlagen"
                        ? "feld-fehler"
                        : "feld-hinweis"
                    }
                    role={
                      datei.upload.status === "fehlgeschlagen"
                        ? "alert"
                        : undefined
                    }
                  >
                    {uploadText(datei)}
                  </p>
                )}
                {datei.upload.status === "fehlgeschlagen" && (
                  <div className="auth-aktionen">
                    <button
                      type="button"
                      className="knopf-leise"
                      onClick={() => void wiederholen(datei.id)}
                    >
                      {datei.name} erneut versuchen
                    </button>
                    <button
                      type="button"
                      className="knopf-leise"
                      onClick={() => fehlgeschlagenEntfernen(datei.id)}
                    >
                      {datei.name} entfernen
                    </button>
                  </div>
                )}
                {bearbeitbar && (
                  <button
                    type="button"
                    className="knopf-leise"
                    onClick={() => dateiEntfernen(datei.id)}
                  >
                    {datei.name} entfernen
                  </button>
                )}
              </li>
            ))}
          </ul>
          {zustand.phase === "sammeln" && zustand.fehler && (
            <p role="alert" className="feld-fehler">
              {zustand.fehler}
            </p>
          )}
          {zustand.phase === "gespeichert" && !festesLied && (
            <output className="feld-hinweis">
              {ziel?.status === "vorhanden"
                ? "Dateien hinzugefügt."
                : "Lied angelegt."}{" "}
              <Link href={`/lied/?id=${encodeURIComponent(zustand.liedId)}`}>
                Zum Lied
              </Link>
            </output>
          )}
          <div className="auth-aktionen">
            <button
              type="submit"
              disabled={!kannSpeichern(zustand) || zustand.phase !== "sammeln"}
            >
              {zustand.phase === "speichern"
                ? "Wird gespeichert …"
                : "Speichern"}
            </button>
            {zustand.phase === "sammeln" && (
              <button
                type="button"
                className="knopf-leise"
                onClick={() => void verwerfen().then(onVerworfen)}
              >
                Entwurf verwerfen
              </button>
            )}
          </div>
        </form>
      )}

      {zustand.phase === "leer" && onOhneDatei && !festesLied && (
        <p className="feld-hinweis">
          <button type="button" className="knopf-leise" onClick={onOhneDatei}>
            ohne Datei anlegen
          </button>
        </p>
      )}
    </div>
  );
}

/**
 * Die Ablage auf der Lied-Seite (#179): Dateien kommen über denselben Ablauf
 * wie bei "Neues Lied" zu genau diesem Lied.
 */
export function DateienZumLied({
  liedId,
  titel,
  onGeaendert,
}: {
  liedId: string;
  titel: string;
  onGeaendert: () => void;
}) {
  // Nach dem Speichern oder Verwerfen beginnt ein frischer Entwurf.
  const [lauf, setLauf] = useState(0);
  return (
    <section
      className="auth-karte lied-ablegen-lied"
      aria-labelledby="lied-ablegen-lied-titel"
    >
      <h2 id="lied-ablegen-lied-titel">Dateien ablegen</h2>
      <DateienAblegen
        key={`${liedId}-${lauf}`}
        festesLied={{ liedId, titel }}
        onVerworfen={() => setLauf((n) => n + 1)}
        onFertig={() => {
          onGeaendert();
          setLauf((n) => n + 1);
        }}
      />
    </section>
  );
}
