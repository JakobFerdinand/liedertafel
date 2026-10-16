"use client";

import { useState } from "react";
import { problemTitel } from "@/lib/assets";
import {
  aendereLied,
  type LiedEingabe,
  type LiedMitDateien,
  legeLiedAn,
} from "@/lib/lieder";

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

function ausLied(lied: LiedMitDateien): LiedEingabe {
  return {
    title: lied.title,
    composer: lied.composer ?? "",
    arranger: lied.arranger ?? "",
    lyricist: lied.lyricist ?? "",
    voiceConfiguration: lied.voiceConfiguration ?? "",
    musicalKey: lied.musicalKey ?? "",
    language: lied.language ?? "",
    lyrics: lied.lyrics ?? "",
    occasion: lied.occasion ?? "",
  };
}

const kurzeFelder: [keyof LiedEingabe, string, string][] = [
  ["composer", "komponist", "Komponist (optional)"],
  ["arranger", "bearbeiter", "Bearbeiter (optional)"],
  ["lyricist", "texter", "Texter (optional)"],
  ["voiceConfiguration", "besetzung", "Besetzung (optional)"],
  ["musicalKey", "tonart", "Tonart (optional)"],
  ["language", "sprache", "Sprache (optional)"],
  ["occasion", "anlass", "Anlass (optional)"],
];

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
  const [werte, setWerte] = useState<LiedEingabe>(lied ? ausLied(lied) : LEER);
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
      if (!lied) setWerte(LEER);
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
          <label htmlFor={`${idPraefix}-${name}`}>{beschriftung}</label>
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
