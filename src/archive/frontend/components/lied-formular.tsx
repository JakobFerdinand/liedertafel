"use client";

import { useState } from "react";
import { patchAuth, postAuth } from "@/lib/auth";
import type { Lied } from "@/lib/songs";

type ListenZeile = { schluessel: number; wert: string };

type LiedFormularWerte = {
  title: string;
  composer: string;
  lyricist: string;
  sprache: string;
  anlass: string;
  arrangementLabel: string;
  versionLabel: string;
  lyrics: string;
  andereTitel: ListenZeile[];
  schlagwoerter: ListenZeile[];
};

const LEER: LiedFormularWerte = {
  title: "",
  composer: "",
  lyricist: "",
  sprache: "",
  anlass: "",
  arrangementLabel: "",
  versionLabel: "",
  lyrics: "",
  andereTitel: [],
  schlagwoerter: [],
};

export function LiedFormular({
  lied,
  absendenText,
  onSuccess,
}: {
  lied?:
    | (Lied & {
        lyrics?: string | null;
        language?: string | null;
        occasion?: string | null;
        tags?: string[];
      })
    | null;
  absendenText: string;
  onSuccess: (lied: Lied, meldung: string) => void;
}) {
  const idPraefix = lied ? `lied-${lied.id}` : "lied";
  // Nur die Detailansicht liefert den bisherigen Liedtext mit; im Katalog
  // bleibt das Feld ohne bekannten Wert.
  const kenntText = lied?.lyrics !== undefined;
  // Gleiches Muster für Sprache, Anlass und Schlagwörter (ARC-023): die
  // Detailansicht kennt die bisherigen Werte, der Katalog nicht.
  const kenntSprache = lied?.language !== undefined;
  const kenntAnlass = lied?.occasion !== undefined;
  const kenntSchlagwoerter = lied?.tags !== undefined;
  const [werte, setWerte] = useState<LiedFormularWerte>(
    lied
      ? {
          title: lied.title,
          composer: lied.composer ?? "",
          lyricist: lied.lyricist ?? "",
          sprache: lied.language ?? "",
          anlass: lied.occasion ?? "",
          arrangementLabel: "",
          versionLabel: "",
          // Nur die Detailansicht kennt den bisherigen Liedtext; im Katalog
          // bleibt das Feld leer und ein leerer Text lässt ihn unverändert.
          lyrics: lied.lyrics ?? "",
          andereTitel: (lied.alternateTitles ?? []).map((wert, index) => ({
            schluessel: index,
            wert,
          })),
          schlagwoerter: (lied.tags ?? []).map((wert, index) => ({
            schluessel: index,
            wert,
          })),
        }
      : LEER,
  );
  const [titelFehler, setTitelFehler] = useState("");
  const [textFehler, setTextFehler] = useState("");
  const [andereFehler, setAndereFehler] = useState("");
  const [spracheFehler, setSpracheFehler] = useState("");
  const [anlassFehler, setAnlassFehler] = useState("");
  const [schlagwortFehler, setSchlagwortFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  function setzen(feld: keyof LiedFormularWerte, wert: string) {
    setWerte((bisher) => ({ ...bisher, [feld]: wert }));
  }

  type ListenFeld = "andereTitel" | "schlagwoerter";

  function zeileÄndern(feld: ListenFeld, index: number, wert: string) {
    setWerte((bisher) => ({
      ...bisher,
      [feld]: bisher[feld].map((zeile, stelle) =>
        stelle === index ? { ...zeile, wert } : zeile,
      ),
    }));
  }

  function zeileHinzufügen(feld: ListenFeld) {
    setWerte((bisher) => {
      const zeilen = bisher[feld];
      if (zeilen.length >= 10) return bisher;
      const schluessel =
        zeilen.reduce((max, zeile) => Math.max(max, zeile.schluessel), -1) + 1;
      return {
        ...bisher,
        [feld]: [...zeilen, { schluessel, wert: "" }],
      };
    });
  }

  function zeileEntfernen(feld: ListenFeld, index: number) {
    setWerte((bisher) => ({
      ...bisher,
      [feld]: bisher[feld].filter((_, stelle) => stelle !== index),
    }));
  }

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setTitelFehler("");
    setTextFehler("");
    setAndereFehler("");
    setSpracheFehler("");
    setAnlassFehler("");
    setSchlagwortFehler("");
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
    const andere = werte.andereTitel.map((zeile) => zeile.wert.trim());
    if (andere.some((eintrag) => eintrag.length > 200)) {
      setAndereFehler("Ein anderer Titel ist zu lang.");
      return;
    }
    const sprache = werte.sprache.trim();
    if (sprache.length > 200) {
      setSpracheFehler("Die Sprache ist zu lang.");
      return;
    }
    const anlass = werte.anlass.trim();
    if (anlass.length > 200) {
      setAnlassFehler("Der Anlass ist zu lang.");
      return;
    }
    // Leere Zeilen fallen still weg, wie bei den anderen Titeln.
    const schlagwoerter = werte.schlagwoerter
      .map((zeile) => zeile.wert.trim())
      .filter(Boolean);
    if (werte.schlagwoerter.some((zeile) => zeile.wert.trim().length > 60)) {
      setSchlagwortFehler("Das Schlagwort ist zu lang.");
      return;
    }
    setBusy(true);
    try {
      const text = werte.lyrics.trim();
      // Bekannter Liedtext (Detailansicht): ein geleertes Feld räumt ihn weg.
      // Unbekannter bisheriger Text (Katalogbearbeitung): nur ein getippter
      // Text wird geschickt, ein leeres Feld lässt den bisherigen
      // unangetastet. Die anderen Titel werden stets als ganze Liste ersetzt.
      const kern = {
        title: titel,
        composer: werte.composer.trim() ? werte.composer.trim() : null,
        lyricist: werte.lyricist.trim() ? werte.lyricist.trim() : null,
      };
      const body: Record<string, unknown> = lied
        ? {
            ...kern,
            alternateTitles: andere.filter((eintrag) => eintrag.length > 0),
          }
        : {
            ...kern,
            language: sprache ? sprache : null,
            occasion: anlass ? anlass : null,
            tags: schlagwoerter,
            arrangementLabel: werte.arrangementLabel.trim()
              ? werte.arrangementLabel.trim()
              : null,
            versionLabel: werte.versionLabel.trim()
              ? werte.versionLabel.trim()
              : null,
          };
      if (lied && (kenntText || text)) body.lyrics = text;
      // Bekannte Werte (Detailansicht) gehen stets mit, ein geleertes Feld
      // räumt sie weg; im Katalog bleibt ein leeres Feld wirkungslos.
      if (lied && (kenntSprache || sprache)) body.language = sprache;
      if (lied && (kenntAnlass || anlass)) body.occasion = anlass;
      if (lied && (kenntSchlagwoerter || schlagwoerter.length > 0))
        body.tags = schlagwoerter;
      const response = lied
        ? await patchAuth(`/api/songs/${encodeURIComponent(lied.id)}`, body)
        : await postAuth("/api/songs", body);
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      const gespeichert = payload?.song as Lied | undefined;
      if (gespeichert) {
        onSuccess(
          gespeichert,
          lied ? "Änderungen gespeichert." : "Lied angelegt.",
        );
        if (!lied) setWerte(LEER);
      } else {
        setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
      }
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
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
        maxLength={300}
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
      <label htmlFor={`${idPraefix}-komponist`}>Komponist (optional)</label>
      <input
        id={`${idPraefix}-komponist`}
        type="text"
        maxLength={300}
        value={werte.composer}
        onChange={(event) => setzen("composer", event.target.value)}
      />
      <label htmlFor={`${idPraefix}-texter`}>Textdichter (optional)</label>
      <input
        id={`${idPraefix}-texter`}
        type="text"
        maxLength={300}
        value={werte.lyricist}
        onChange={(event) => setzen("lyricist", event.target.value)}
      />
      <label htmlFor={`${idPraefix}-sprache`}>Sprache (optional)</label>
      <input
        id={`${idPraefix}-sprache`}
        type="text"
        maxLength={200}
        value={werte.sprache}
        onChange={(event) => setzen("sprache", event.target.value)}
        aria-invalid={spracheFehler ? true : undefined}
        aria-describedby={`${idPraefix}-sprache-fehler`}
      />
      {spracheFehler && (
        <p
          id={`${idPraefix}-sprache-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {spracheFehler}
        </p>
      )}
      <label htmlFor={`${idPraefix}-anlass`}>Anlass (optional)</label>
      <input
        id={`${idPraefix}-anlass`}
        type="text"
        maxLength={200}
        value={werte.anlass}
        onChange={(event) => setzen("anlass", event.target.value)}
        aria-invalid={anlassFehler ? true : undefined}
        aria-describedby={`${idPraefix}-anlass-fehler`}
      />
      {anlassFehler && (
        <p
          id={`${idPraefix}-anlass-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {anlassFehler}
        </p>
      )}
      {lied ? (
        <>
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
            {kenntText
              ? "Anfangsworte genügen. Ein geleerter Text wird entfernt."
              : "Anfangsworte genügen. Leer lassen ändert den bisherigen Text nicht."}
          </p>
          <fieldset className="lied-andere-titel">
            <legend>Andere Titel</legend>
            <p className="feld-hinweis">
              Beispielsweise frühere Namen oder Kürzel, unter denen das Lied
              bekannt ist.
            </p>
            {werte.andereTitel.map((zeile, index) => (
              <div key={zeile.schluessel} className="lied-anderer-titel">
                <input
                  id={`${idPraefix}-anderer-titel-${index}`}
                  type="text"
                  maxLength={200}
                  value={zeile.wert}
                  onChange={(event) =>
                    zeileÄndern("andereTitel", index, event.target.value)
                  }
                  aria-label={`Anderer Titel ${index + 1}`}
                />
                <button
                  type="button"
                  onClick={() => zeileEntfernen("andereTitel", index)}
                  aria-label={`Anderen Titel ${index + 1} entfernen`}
                >
                  Entfernen
                </button>
              </div>
            ))}
            {andereFehler && (
              <p role="alert" className="feld-fehler">
                {andereFehler}
              </p>
            )}
            <button
              type="button"
              disabled={werte.andereTitel.length >= 10}
              onClick={() => zeileHinzufügen("andereTitel")}
            >
              Anderen Titel hinzufügen
            </button>
          </fieldset>
        </>
      ) : (
        <>
          <label htmlFor={`${idPraefix}-arrangement`}>
            Arrangement (optional)
          </label>
          <input
            id={`${idPraefix}-arrangement`}
            type="text"
            maxLength={300}
            placeholder="Standardfassung"
            value={werte.arrangementLabel}
            onChange={(event) => setzen("arrangementLabel", event.target.value)}
          />
          <label htmlFor={`${idPraefix}-version`}>
            Musikalische Fassung (optional)
          </label>
          <input
            id={`${idPraefix}-version`}
            type="text"
            maxLength={300}
            placeholder="Standardfassung"
            value={werte.versionLabel}
            onChange={(event) => setzen("versionLabel", event.target.value)}
          />
          <p className="feld-hinweis">
            Ohne Angabe werden Arrangement und Fassung als „Standardfassung"
            angelegt.
          </p>
        </>
      )}
      <fieldset className="lied-listen">
        <legend>Schlagwörter</legend>
        <p className="feld-hinweis">
          Freie Merkworte, unter denen sich das Lied im Katalog wiederfinden
          lässt.
        </p>
        {werte.schlagwoerter.map((zeile, index) => (
          <div key={zeile.schluessel} className="lied-anderer-titel">
            <input
              id={`${idPraefix}-schlagwort-${index}`}
              type="text"
              maxLength={60}
              value={zeile.wert}
              onChange={(event) =>
                zeileÄndern("schlagwoerter", index, event.target.value)
              }
              aria-label={`Schlagwort ${index + 1}`}
            />
            <button
              type="button"
              onClick={() => zeileEntfernen("schlagwoerter", index)}
              aria-label={`Schlagwort ${index + 1} entfernen`}
            >
              Entfernen
            </button>
          </div>
        ))}
        {schlagwortFehler && (
          <p role="alert" className="feld-fehler">
            {schlagwortFehler}
          </p>
        )}
        <button
          type="button"
          disabled={werte.schlagwoerter.length >= 10}
          onClick={() => zeileHinzufügen("schlagwoerter")}
        >
          Schlagwort hinzufügen
        </button>
      </fieldset>
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
