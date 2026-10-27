"use client";

import { useState } from "react";
import {
  aendereLied,
  eingabeAusLied,
  type LiedEingabe,
  type LiedMitDateien,
  leerEingabe,
  legeLiedAn,
  liedEingabeFelder,
  liedFeldBeschriftung,
} from "@/lib/lieder";
import { problemTitel } from "@/lib/uebertragung";

/**
 * Die Zeilen der Kurzfelder aus der Feldliste und den Beschriftungen der
 * Eingabe (#192): ohne den Titel (oben) und den Liedtext (unten die Fläche).
 * Hier steht allein, dass die Felder von Hand freiwillig sind.
 */
const kurzeFelder: [keyof LiedEingabe, string, string][] = liedEingabeFelder
  .filter((feld) => feld !== "title" && feld !== "lyrics")
  .map((feld) => [feld, ...liedFeldBeschriftung[feld]]);

/**
 * Formular für ein Lied von Hand (#166): Titel, Komponist, Bearbeiter, Texter,
 * Besetzung, Tonart, Sprache, Liedtext und Anlass. Ohne `lied` legt es ein
 * neues an, mit `lied` ändert es das vorhandene.
 */
export function LiedFormular({
  lied,
  absendenText,
  onSuccess,
}: {
  lied?: LiedMitDateien | null;
  absendenText: string;
  onSuccess: (lied: LiedMitDateien, meldung: string) => void;
}) {
  const idPraefix = lied ? `lied-${lied.id}` : "lied";
  const [werte, setWerte] = useState<LiedEingabe>(
    lied ? eingabeAusLied(lied) : leerEingabe(),
  );
  const [titelFehler, setTitelFehler] = useState("");
  const [textFehler, setTextFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  function setzen(feld: keyof LiedEingabe, wert: string) {
    setWerte((bisher) => ({ ...bisher, [feld]: wert }));
  }

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setTitelFehler("");
    setTextFehler("");
    setHinweis("");
    const titel = werte.title.trim();
    if (!titel) {
      setTitelFehler("Bitte einen Titel eingeben.");
      return;
    }
    if (werte.lyrics.trim().length > 5000) {
      setTextFehler("Der Liedtext ist zu lang.");
      return;
    }
    setBusy(true);
    try {
      const gespeichert = lied
        ? await aendereLied(lied.id, werte, lied.rowVersion)
        : await legeLiedAn(werte);
      onSuccess(
        gespeichert,
        lied ? "Änderungen gespeichert." : "Lied angelegt.",
      );
      if (!lied) setWerte(leerEingabe());
    } catch (ursache) {
      setHinweis(
        await problemTitel(
          ursache,
          "Das hat nicht geklappt. Bitte erneut versuchen.",
        ),
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={speichern} noValidate>
      <label htmlFor={`${idPraefix}-titel`}>Titel</label>
      <input
        id={`${idPraefix}-titel`}
        type="text"
        required
        maxLength={200}
        value={werte.title}
        onChange={(event) => setzen("title", event.target.value)}
        aria-invalid={titelFehler ? true : undefined}
        aria-describedby={`${idPraefix}-titel-fehler`}
      />
      {titelFehler && (
        <p
          id={`${idPraefix}-titel-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {titelFehler}
        </p>
      )}
      {kurzeFelder.map(([feld, name, beschriftung]) => (
        <div key={feld}>
          <label htmlFor={`${idPraefix}-${name}`}>
            {beschriftung} (optional)
          </label>
          <input
            id={`${idPraefix}-${name}`}
            type="text"
            maxLength={200}
            value={werte[feld]}
            onChange={(event) => setzen(feld, event.target.value)}
          />
        </div>
      ))}
      <label htmlFor={`${idPraefix}-liedtext`}>Liedtext (optional)</label>
      <textarea
        id={`${idPraefix}-liedtext`}
        rows={4}
        maxLength={5000}
        value={werte.lyrics}
        onChange={(event) => setzen("lyrics", event.target.value)}
        aria-invalid={textFehler ? true : undefined}
        aria-describedby={`${idPraefix}-liedtext-hinweis`}
      />
      {textFehler && (
        <p
          id={`${idPraefix}-liedtext-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {textFehler}
        </p>
      )}
      <p id={`${idPraefix}-liedtext-hinweis`} className="feld-hinweis">
        Anfangsworte genügen. Ein geleerter Text wird entfernt.
      </p>
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
      <div className="auth-aktionen">
        <button type="submit" disabled={busy}>
          {busy ? "Wird gespeichert …" : absendenText}
        </button>
      </div>
    </form>
  );
}
