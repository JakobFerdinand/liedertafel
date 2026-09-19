"use client";

import type { LiedDetails } from "@/lib/songs";

type FassungsWahlProps = {
  lied: LiedDetails;
  arrangementId: string;
  versionId: string;
  onSelect: (arrangementId: string, versionId: string) => void;
};

export function FassungsWahl({
  lied,
  arrangementId,
  versionId,
  onSelect,
}: FassungsWahlProps) {
  return (
    <fieldset className="fassungs-wahl">
      <legend className="visually-hidden">Fassung auswählen</legend>
      {lied.arrangements.map((arrangement) => {
        const gewaehlt = arrangement.id === arrangementId;
        return (
          <section
            key={arrangement.id}
            className="fassungs-gruppe"
            data-gewaehlt={gewaehlt ? "true" : undefined}
            aria-labelledby={`fassungs-gruppe-${arrangement.id}`}
          >
            <h3 id={`fassungs-gruppe-${arrangement.id}`}>
              {arrangement.label}
            </h3>
            {arrangement.arranger && (
              <p className="feld-hinweis">
                Bearbeitung: {arrangement.arranger}
              </p>
            )}
            {arrangement.voiceConfiguration && (
              <p className="feld-hinweis">
                Stimmkonfiguration: {arrangement.voiceConfiguration}
              </p>
            )}
            {arrangement.musicalVersions.length > 0 && (
              <ul className="fassungs-liste">
                {arrangement.musicalVersions.map((fassung) => (
                  <li key={fassung.id}>
                    <button
                      type="button"
                      aria-pressed={gewaehlt && fassung.id === versionId}
                      onClick={() => onSelect(arrangement.id, fassung.id)}
                    >
                      {fassung.label}
                      {fassung.creator ? ` · ${fassung.creator}` : ""}
                      {fassung.musicalKey
                        ? ` · Tonart: ${fassung.musicalKey}`
                        : ""}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        );
      })}
    </fieldset>
  );
}
