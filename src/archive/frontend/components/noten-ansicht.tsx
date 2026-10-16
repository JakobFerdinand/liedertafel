"use client";

import { useEffect, useRef, useState } from "react";
import { groesseText } from "@/lib/assets";
import { type DateiZugriff, fetchDateiZugriff } from "@/lib/dateien";
import { dateiTitel, type LiedDatei } from "@/lib/lieder";

/**
 * Eine Noten-Datei lesen (#166): PDF im Rahmen der Seite, auf Wunsch im
 * Vollbild, plus Herunterladen. Der Zugriff wird erst beim Anzeigen geholt.
 * Andere Dateiarten haben eigene Komponenten (Spieler: #167).
 */
export function NotenAnsicht({ datei }: { datei: LiedDatei }) {
  const rahmenRef = useRef<HTMLIFrameElement>(null);
  const [zugriff, setZugriff] = useState<DateiZugriff | null>(null);
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");
  // Vollbild nur anbieten, wenn der Browser es freigibt; beim statischen
  // Rendern unbekannt, darum erst im Effekt ermitteln.
  const [vollbildMoeglich, setVollbildMoeglich] = useState(false);
  const titel = dateiTitel(datei);

  useEffect(() => {
    setVollbildMoeglich(document.fullscreenEnabled === true);
  }, []);

  async function anzeigen() {
    setBusy(true);
    setFehler("");
    try {
      setZugriff(await fetchDateiZugriff(datei.id));
    } catch (ursache) {
      setFehler(
        ursache instanceof Response && ursache.status === 404
          ? "Für diese Noten liegt keine abrufbare Datei vor."
          : "Noten konnten nicht geladen werden. Bitte erneut versuchen.",
      );
    } finally {
      setBusy(false);
    }
  }

  function vollbild() {
    // Eine Ablehnung (etwa abgebrochene Geste) bleibt still — der Rahmen bleibt.
    void rahmenRef.current?.requestFullscreen().catch(() => {});
  }

  return (
    <article className="material-eintrag" aria-label={`Noten · ${titel}`}>
      <p className="material-stimme">{titel}</p>
      <p className="noten-info">
        {datei.isPartitur ? "Partitur" : "Stimme"} ·{" "}
        {groesseText(datei.sizeBytes)} · PDF
      </p>
      {datei.description?.trim() && (
        <p className="material-beschreibung">{datei.description.trim()}</p>
      )}
      <div className="noten-aktionen">
        {zugriff ? (
          <>
            <iframe
              ref={rahmenRef}
              className="noten-ansicht"
              src={zugriff.viewUrl}
              title={`Noten (PDF) · ${titel}`}
              allow="fullscreen"
            />
            {vollbildMoeglich && (
              <button type="button" className="knopf-leise" onClick={vollbild}>
                Vollbild
              </button>
            )}
            <a className="noten-laden" href={zugriff.downloadUrl} download>
              Herunterladen
            </a>
          </>
        ) : (
          <button type="button" onClick={() => void anzeigen()} disabled={busy}>
            {busy ? "Noten werden vorbereitet …" : "Noten anzeigen"}
          </button>
        )}
      </div>
      {fehler && (
        <output aria-live="polite" className="feld-fehler">
          {fehler}
        </output>
      )}
    </article>
  );
}
