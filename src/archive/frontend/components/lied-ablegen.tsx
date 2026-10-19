"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { EntwurfFehler, ladeEntwurf } from "@/lib/lied-entwuerfe";
import type { Gespeichert } from "@/lib/lied-entwurf/gespeichert";
import {
  type DateiEntwurf,
  fehlgeschlagene,
  kannSpeichern,
  type LiedFeld,
  type Zustand,
} from "@/lib/lied-entwurf/reducer";
import { useLiedEntwurf } from "@/lib/lied-entwurf/use-lied-entwurf";
import { groesseText } from "@/lib/uebertragung";

const dateiTypen: [string, string][] = [
  ["partitur", "Partitur"],
  ["noten", "Noten"],
  ["uebe", "Übe-Datei"],
  ["aufnahme", "Aufnahme"],
  ["sonstiges", "Sonstiges"],
];

const liedFelder: [LiedFeld, string, "kurz" | "lang"][] = [
  ["composer", "Komponist", "kurz"],
  ["arranger", "Bearbeiter", "kurz"],
  ["lyricist", "Texter", "kurz"],
  ["voiceConfiguration", "Besetzung", "kurz"],
  ["musicalKey", "Tonart", "kurz"],
  ["language", "Sprache", "kurz"],
  ["lyrics", "Liedtext", "lang"],
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

function DateienAblegen({
  gespeichert,
  onOhneDatei,
  onVerworfen,
}: {
  gespeichert?: Gespeichert;
  onOhneDatei: () => void;
  onVerworfen: () => void;
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
  } = useLiedEntwurf(gespeichert);

  const gespeichertId =
    zustand.phase === "gespeichert" && fehlgeschlagene(zustand).length === 0
      ? zustand.liedId
      : null;
  useEffect(() => {
    if (gespeichertId) {
      router.push(`/lied/?id=${encodeURIComponent(gespeichertId)}`);
    }
  }, [gespeichertId, router]);

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
            aria-label="Dateien auswählen"
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
          <label htmlFor={`${idPraefix}-titel`}>Titel</label>
          <input
            id={`${idPraefix}-titel`}
            type="text"
            maxLength={200}
            value={zustand.lied.title}
            disabled={!bearbeitbar}
            onChange={(event) => liedFeldAendern("title", event.target.value)}
          />
          <VorschlagMarke zustand={zustand} feld="title" />
          {liedFelder.map(([feld, beschriftung, art]) => (
            <div key={feld}>
              <label htmlFor={`${idPraefix}-${feld}`}>{beschriftung}</label>
              {art === "lang" ? (
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
          {zustand.phase === "gespeichert" && (
            <output className="feld-hinweis">
              Lied angelegt.{" "}
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

      {zustand.phase === "leer" && (
        <p className="feld-hinweis">
          <button type="button" className="knopf-leise" onClick={onOhneDatei}>
            ohne Datei anlegen
          </button>
        </p>
      )}
    </div>
  );
}
