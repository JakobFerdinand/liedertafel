"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import {
  type DateiEntwurf,
  fehlgeschlagene,
  kannSpeichern,
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
export function LiedAblegen() {
  const [vonHand, setVonHand] = useState(false);
  const router = useRouter();

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
  return <DateienAblegen onOhneDatei={() => setVonHand(true)} />;
}

function DateienAblegen({ onOhneDatei }: { onOhneDatei: () => void }) {
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
  } = useLiedEntwurf();

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
                {datei.upload.status === "laedt" && datei.upload.gesamt > 0 && (
                  <div className="material-fortschritt" aria-hidden="true">
                    <span
                      style={{
                        width: `${Math.round((datei.upload.bytes / datei.upload.gesamt) * 100)}%`,
                      }}
                    />
                  </div>
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
