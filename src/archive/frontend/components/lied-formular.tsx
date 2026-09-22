"use client";

import { useState } from "react";
import { patchAuth, postAuth } from "@/lib/auth";
import type { Lied } from "@/lib/songs";

type AndererTitelZeile = { schluessel: number; wert: string };

type LiedFormularWerte = {
  title: string;
  composer: string;
  lyricist: string;
  arrangementLabel: string;
  versionLabel: string;
  lyrics: string;
  andereTitel: AndererTitelZeile[];
};

const LEER: LiedFormularWerte = {
  title: "",
  composer: "",
  lyricist: "",
  arrangementLabel: "",
  versionLabel: "",
  lyrics: "",
  andereTitel: [],
};

export function LiedFormular({
  lied,
  absendenText,
  onSuccess,
}: {
  lied?: (Lied & { lyrics?: string | null }) | null;
  absendenText: string;
  onSuccess: (lied: Lied, meldung: string) => void;
}) {
  const idPraefix = lied ? `lied-${lied.id}` : "lied";
  const [werte, setWerte] = useState<LiedFormularWerte>(
    lied
      ? {
          title: lied.title,
          composer: lied.composer ?? "",
          lyricist: lied.lyricist ?? "",
          arrangementLabel: "",
          versionLabel: "",
          // Nur die Detailansicht kennt den bisherigen Liedtext; im Katalog
          // bleibt das Feld leer und ein leerer Text lässt ihn unverändert.
          lyrics: lied.lyrics ?? "",
          andereTitel: (lied.alternateTitles ?? []).map((wert, index) => ({
            schluessel: index,
            wert,
          })),
        }
      : LEER,
  );
  const [titelFehler, setTitelFehler] = useState("");
  const [textFehler, setTextFehler] = useState("");
  const [andereFehler, setAndereFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  function setzen(feld: keyof LiedFormularWerte, wert: string) {
    setWerte((bisher) => ({ ...bisher, [feld]: wert }));
  }

  function anderenTitelÄndern(index: number, wert: string) {
    setWerte((bisher) => ({
      ...bisher,
      andereTitel: bisher.andereTitel.map((zeile, stelle) =>
        stelle === index ? { ...zeile, wert } : zeile,
      ),
    }));
  }

  function anderenTitelHinzufügen() {
    setWerte((bisher) => {
      if (bisher.andereTitel.length >= 10) return bisher;
      const schluessel =
        bisher.andereTitel.reduce(
          (max, zeile) => Math.max(max, zeile.schluessel),
          -1,
        ) + 1;
      return {
        ...bisher,
        andereTitel: [...bisher.andereTitel, { schluessel, wert: "" }],
      };
    });
  }

  function anderenTitelEntfernen(index: number) {
    setWerte((bisher) => ({
      ...bisher,
      andereTitel: bisher.andereTitel.filter((_, stelle) => stelle !== index),
    }));
  }

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setTitelFehler("");
    setTextFehler("");
    setAndereFehler("");
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
    setBusy(true);
    try {
      const kern = {
        title: titel,
        composer: werte.composer.trim() ? werte.composer.trim() : null,
        lyricist: werte.lyricist.trim() ? werte.lyricist.trim() : null,
      };
      const body = lied
        ? {
            ...kern,
            // Leerer Liedtext heißt „unverändert lassen"; zum Entfernen über
            // die Oberfläche trägt man in der Detailansicht einen neuen Text
            // ein. Die anderen Titel werden stets als ganze Liste ersetzt.
            lyrics: werte.lyrics.trim() ? werte.lyrics.trim() : null,
            alternateTitles: andere.filter((eintrag) => eintrag.length > 0),
          }
        : {
            ...kern,
            arrangementLabel: werte.arrangementLabel.trim()
              ? werte.arrangementLabel.trim()
              : null,
            versionLabel: werte.versionLabel.trim()
              ? werte.versionLabel.trim()
              : null,
          };
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
      {lied ? (
        <>
          <label htmlFor={`${idPraefix}-liedtext`}>Liedtext (optional)</label>
          <textarea
            id={`${idPraefix}-liedtext`}
            rows={4}
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
            Anfangsworte genügen. Leer lassen ändert den bisherigen Text nicht.
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
                    anderenTitelÄndern(index, event.target.value)
                  }
                  aria-label={`Anderer Titel ${index + 1}`}
                />
                <button
                  type="button"
                  onClick={() => anderenTitelEntfernen(index)}
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
              onClick={anderenTitelHinzufügen}
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
