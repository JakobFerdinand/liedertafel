"use client";

import { useState } from "react";
import { DateiAktionen } from "@/components/datei-aktionen";
import { DateiSpieler } from "@/components/datei-spieler";
import { NotenAnsicht } from "@/components/noten-ansicht";
import { groesseText } from "@/lib/assets";
import { fetchDateiZugriff } from "@/lib/dateien";
import { dateiArten, dateiTitel, type LiedDatei } from "@/lib/lieder";

/**
 * Sonstige Dateien (#166): Name, Größe und Herunterladen. Übe-Dateien und
 * Aufnahmen spielt DateiSpieler (#167); #168 ergänzt
 * Ersetzen und Löschen; beides in eigenen Komponenten, hier nur die Einhängung.
 */
function EinfacheDatei({ datei }: { datei: LiedDatei }) {
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");

  async function herunterladen() {
    setBusy(true);
    setFehler("");
    try {
      const zugriff = await fetchDateiZugriff(datei.id);
      window.location.assign(zugriff.downloadUrl);
    } catch {
      setFehler("Die Datei konnte nicht geladen werden.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <article className="material-eintrag" aria-label={dateiTitel(datei)}>
      <p className="material-stimme">{dateiTitel(datei)}</p>
      <p className="noten-info">{groesseText(datei.sizeBytes)}</p>
      <div className="noten-aktionen">
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void herunterladen()}
          disabled={busy}
        >
          Herunterladen
        </button>
      </div>
      {fehler && (
        <output aria-live="polite" className="feld-fehler">
          {fehler}
        </output>
      )}
    </article>
  );
}

/** Die Dateien eines Liedes, gruppiert nach Art (Noten, Übe-Dateien, …). */
export function DateiListe({
  dateien,
  liedId,
  onGeaendert,
}: {
  dateien: LiedDatei[];
  /** Mit `liedId` und `onGeaendert` zeigt jede Datei Ersetzen und Löschen (#168, nur Redaktion). */
  liedId?: string;
  onGeaendert?: () => void;
}) {
  const [spielendeDatei, setSpielendeDatei] = useState<string | null>(null);
  if (dateien.length === 0) {
    return <p className="noten-info">Noch keine Dateien hochgeladen.</p>;
  }
  return (
    <div className="material-liste">
      {dateiArten.map(([art, titel]) => {
        const gruppe = dateien.filter((datei) => datei.kind === art);
        if (gruppe.length === 0) return null;
        return (
          <div className="material-gruppe" key={art}>
            <h4>{titel}</h4>
            {gruppe.map((datei) => (
              <div key={datei.id}>
                {art === "noten" ? (
                  <NotenAnsicht datei={datei} />
                ) : art === "uebe" || art === "aufnahme" ? (
                  <DateiSpieler
                    datei={datei}
                    aktiv={spielendeDatei === datei.id}
                    onAbspielen={() => setSpielendeDatei(datei.id)}
                  />
                ) : (
                  <EinfacheDatei datei={datei} />
                )}
                {liedId && onGeaendert && (
                  <DateiAktionen
                    liedId={liedId}
                    datei={datei}
                    onGeaendert={onGeaendert}
                  />
                )}
              </div>
            ))}
          </div>
        );
      })}
    </div>
  );
}
