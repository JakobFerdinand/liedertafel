"use client";

import { useRef, useState } from "react";
import { AbbruchFehler, MaterialFehler, problemTitel } from "@/lib/assets";
import { ersetzeDatei, loescheDatei } from "@/lib/dateien";
import { dateiTitel, type LiedDatei } from "@/lib/lieder";

/**
 * Ersetzen und Löschen einer Datei (#168), nur für die Redaktion. Die ersetzte
 * oder gelöschte Datei wandert in den Papierkorb; das Lied zeigt danach nur
 * noch den aktuellen Stand (`onGeaendert` lädt die Dateiliste neu).
 */
export function DateiAktionen({
  liedId,
  datei,
  onGeaendert,
}: {
  liedId: string;
  datei: LiedDatei;
  onGeaendert: () => void;
}) {
  const eingabeRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [bestaetigen, setBestaetigen] = useState(false);
  const [status, setStatus] = useState("");
  const [fehler, setFehler] = useState("");
  const titel = dateiTitel(datei);

  async function ersetzen(event: React.ChangeEvent<HTMLInputElement>) {
    const gewaehlt = event.target.files?.[0];
    event.target.value = "";
    if (!gewaehlt) return;
    setBusy(true);
    setFehler("");
    setStatus("Neue Datei wird übertragen …");
    try {
      await ersetzeDatei(liedId, datei, gewaehlt, (schritt) =>
        setStatus(
          schritt === "uebertragen"
            ? "Neue Datei wird übertragen …"
            : "Neue Datei wird geprüft …",
        ),
      );
      setStatus("");
      onGeaendert();
    } catch (ursache) {
      setStatus("");
      setFehler(
        ursache instanceof AbbruchFehler
          ? "Das Ersetzen wurde abgebrochen."
          : ursache instanceof MaterialFehler
            ? ursache.message
            : await problemTitel(
                ursache,
                "Das Ersetzen konnte nicht abgeschlossen werden. Bitte erneut versuchen.",
              ),
      );
    } finally {
      setBusy(false);
    }
  }

  async function loeschen() {
    setBusy(true);
    setFehler("");
    try {
      await loescheDatei(datei.id, datei.rowVersion);
      onGeaendert();
    } catch (ursache) {
      setBestaetigen(false);
      const veraltet = ursache instanceof Response && ursache.status === 409;
      setFehler(
        veraltet
          ? "Die Datei wurde inzwischen geändert. Die Liste wird neu geladen."
          : await problemTitel(
              ursache,
              "Die Datei konnte nicht gelöscht werden.",
            ),
      );
      if (veraltet) onGeaendert();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="datei-aktionen">
      <input
        ref={eingabeRef}
        type="file"
        hidden
        aria-label={`Neue Datei für ${titel}`}
        accept={datei.kind === "noten" ? "application/pdf,.pdf" : undefined}
        onChange={(event) => void ersetzen(event)}
      />
      {bestaetigen ? (
        <>
          <span className="noten-info">„{titel}“ in den Papierkorb legen?</span>
          <button type="button" onClick={() => void loeschen()} disabled={busy}>
            Ja, löschen
          </button>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => setBestaetigen(false)}
            disabled={busy}
          >
            Abbrechen
          </button>
        </>
      ) : (
        <>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => eingabeRef.current?.click()}
            disabled={busy}
            aria-label={`Ersetzen · ${titel}`}
          >
            Ersetzen
          </button>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => setBestaetigen(true)}
            disabled={busy}
            aria-label={`Löschen · ${titel}`}
          >
            Löschen
          </button>
        </>
      )}
      {status && (
        <output aria-live="polite" className="noten-info">
          {status}
        </output>
      )}
      {fehler && (
        <output aria-live="polite" className="feld-fehler">
          {fehler}
        </output>
      )}
    </div>
  );
}
