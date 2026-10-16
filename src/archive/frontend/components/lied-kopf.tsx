"use client";

import type { LiedMitDateien } from "@/lib/lieder";

/** Titel und Angaben eines Liedes (#166); der Liedtext klappt auf. */
export function LiedKopf({ lied }: { lied: LiedMitDateien }) {
  const urheber = [
    lied.composer ? `Komponist: ${lied.composer}` : null,
    lied.arranger ? `Bearbeiter: ${lied.arranger}` : null,
    lied.lyricist ? `Text: ${lied.lyricist}` : null,
  ].filter(Boolean);
  const angaben = [
    lied.voiceConfiguration ? `Besetzung: ${lied.voiceConfiguration}` : null,
    lied.musicalKey ? `Tonart: ${lied.musicalKey}` : null,
    lied.language ? `Sprache: ${lied.language}` : null,
    lied.occasion ? `Anlass: ${lied.occasion}` : null,
  ].filter(Boolean);
  return (
    <article className="lied-ansicht">
      <h1>{lied.title}</h1>
      {urheber.length > 0 && (
        <p className="lieder-urheber">{urheber.join(" · ")}</p>
      )}
      {angaben.length > 0 && (
        <p className="lieder-urheber">{angaben.join(" · ")}</p>
      )}
      {lied.lyrics?.trim() && (
        <details className="lied-liedtext">
          <summary>Liedtext</summary>
          <p className="lied-liedtext-inhalt">{lied.lyrics}</p>
        </details>
      )}
    </article>
  );
}
