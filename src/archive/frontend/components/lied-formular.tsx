"use client";

import { useState } from "react";
import { postAuth } from "@/lib/auth";
import type { Lied } from "@/lib/songs";

type LiedFormularWerte = {
  title: string;
  composer: string;
  lyricist: string;
  arrangementLabel: string;
  versionLabel: string;
};

const LEER: LiedFormularWerte = {
  title: "",
  composer: "",
  lyricist: "",
  arrangementLabel: "",
  versionLabel: "",
};

export function LiedFormular({
  lied,
  beschriftung,
  absendenText,
  onSuccess,
}: {
  lied?: Lied | null;
  beschriftung: string;
  absendenText: string;
  onSuccess: (lied: Lied, meldung: string) => void;
}) {
  const [werte, setWerte] = useState<LiedFormularWerte>(
    lied
      ? {
          title: lied.title,
          composer: lied.composer ?? "",
          lyricist: lied.lyricist ?? "",
          arrangementLabel: "",
          versionLabel: "",
        }
      : LEER,
  );
  const [titelFehler, setTitelFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  function setzen(feld: keyof LiedFormularWerte, wert: string) {
    setWerte((bisher) => ({ ...bisher, [feld]: wert }));
  }

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setTitelFehler("");
    setHinweis("");
    const titel = werte.title.trim();
    if (!titel) {
      setTitelFehler("Bitte einen Titel eingeben.");
      return;
    }
    setBusy(true);
    try {
      const body = lied
        ? {
            title: titel,
            composer: werte.composer.trim() ? werte.composer.trim() : null,
            lyricist: werte.lyricist.trim() ? werte.lyricist.trim() : null,
          }
        : {
            title: titel,
            composer: werte.composer.trim() ? werte.composer.trim() : null,
            lyricist: werte.lyricist.trim() ? werte.lyricist.trim() : null,
            arrangementLabel: werte.arrangementLabel.trim()
              ? werte.arrangementLabel.trim()
              : null,
            versionLabel: werte.versionLabel.trim()
              ? werte.versionLabel.trim()
              : null,
          };
      const response = lied
        ? await postAuth(`/api/songs/${encodeURIComponent(lied.id)}`, body)
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
      {lied ? null : <h3>{beschriftung}</h3>}
      <label htmlFor={lied ? `lied-titel-${lied.id}` : "lied-titel"}>
        Titel
      </label>
      <input
        id={lied ? `lied-titel-${lied.id}` : "lied-titel"}
        type="text"
        required
        maxLength={300}
        value={werte.title}
        onChange={(event) => setzen("title", event.target.value)}
        aria-invalid={titelFehler ? true : undefined}
        aria-describedby={
          lied ? `lied-titel-${lied.id}-fehler` : "lied-titel-fehler"
        }
      />
      {titelFehler && (
        <p
          id={lied ? `lied-titel-${lied.id}-fehler` : "lied-titel-fehler"}
          role="alert"
          className="feld-fehler"
        >
          {titelFehler}
        </p>
      )}
      <label htmlFor={lied ? `lied-komponist-${lied.id}` : "lied-komponist"}>
        Komponist (optional)
      </label>
      <input
        id={lied ? `lied-komponist-${lied.id}` : "lied-komponist"}
        type="text"
        maxLength={300}
        value={werte.composer}
        onChange={(event) => setzen("composer", event.target.value)}
      />
      <label htmlFor={lied ? `lied-texter-${lied.id}` : "lied-texter"}>
        Textdichter (optional)
      </label>
      <input
        id={lied ? `lied-texter-${lied.id}` : "lied-texter"}
        type="text"
        maxLength={300}
        value={werte.lyricist}
        onChange={(event) => setzen("lyricist", event.target.value)}
      />
      {lied ? null : (
        <>
          <label htmlFor="lied-arrangement">Arrangement (optional)</label>
          <input
            id="lied-arrangement"
            type="text"
            maxLength={300}
            placeholder="Standardfassung"
            value={werte.arrangementLabel}
            onChange={(event) => setzen("arrangementLabel", event.target.value)}
          />
          <label htmlFor="lied-version">Musikalische Fassung (optional)</label>
          <input
            id="lied-version"
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
