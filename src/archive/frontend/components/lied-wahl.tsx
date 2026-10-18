"use client";

// Liedauswahl für Programm, Nachweise und Bestätigung (#170): eine Suche im
// Liederverzeichnis, ein Treffer ist ein Lied. Jede neue Suche bricht die
// vorige ab, ältere Antworten dürfen jüngere Ergebnisse nicht überschreiben,
// und nach dem Einklappen wird kein Zustand mehr gesetzt.

import { useEffect, useRef, useState } from "react";
import { fetchLiedSuche } from "@/lib/lieder";

const katalogFehler = "Der Katalog antwortet nicht. Bitte erneut versuchen.";

export function LiedWahl({
  onGewaehlt,
  kennung = "programm-lied-suche",
  beschriftung = "Lied aus dem Katalog suchen",
  klasse = "programm",
}: {
  onGewaehlt: (liedId: string, titel: string) => void;
  // Eigene Feld-Id, wenn mehrere Liedsuchen auf einer Seite stehen
  // (Programm-Werkbank, Nachweise und Aufführungsbestätigung).
  kennung?: string;
  beschriftung?: string;
  // Vorhandener Stilpräfix des Bereichs: "programm" oder "belege".
  klasse?: "programm" | "belege";
}) {
  const [suche, setSuche] = useState("");
  const [ergebnisse, setErgebnisse] = useState<
    Array<{ id: string; title: string }>
  >([]);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);
  const laufNummer = useRef(0);
  const laufendeAbbruch = useRef<AbortController | null>(null);
  const lebt = useRef(true);

  useEffect(() => {
    lebt.current = true;
    return () => {
      lebt.current = false;
      laufendeAbbruch.current?.abort();
    };
  }, []);

  async function suchen(event: React.FormEvent) {
    event.preventDefault();
    const nummer = laufNummer.current + 1;
    laufNummer.current = nummer;
    laufendeAbbruch.current?.abort();
    const abbruch = new AbortController();
    laufendeAbbruch.current = abbruch;
    setBusy(true);
    setFehler("");
    try {
      const antwort = await fetchLiedSuche(
        suche.trim() || null,
        1,
        abbruch.signal,
      );
      if (!lebt.current || nummer !== laufNummer.current) return;
      setErgebnisse(
        antwort.lieder.map((eintrag) => ({
          id: eintrag.id,
          title: eintrag.title,
        })),
      );
      if (antwort.lieder.length === 0) setFehler("Kein Lied gefunden.");
    } catch {
      // Abgebrochene oder überholte Antworten ändern nichts mehr.
      if (!lebt.current || nummer !== laufNummer.current) return;
      setFehler(katalogFehler);
    } finally {
      if (lebt.current && nummer === laufNummer.current) setBusy(false);
    }
  }

  return (
    <form
      className={`${klasse}-lied-wahl`}
      onSubmit={(event) => void suchen(event)}
    >
      <label htmlFor={kennung}>{beschriftung}</label>
      <div className={`${klasse}-lied-suche-reihe`}>
        <input
          id={kennung}
          type="search"
          value={suche}
          onChange={(event) => setSuche(event.target.value)}
        />
        <button type="submit" disabled={busy}>
          {busy ? "Wird gesucht …" : "Suchen"}
        </button>
      </div>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {ergebnisse.length > 0 && (
        <div className={`${klasse}-lied-treffer`}>
          {ergebnisse.map((eintrag) => (
            <button
              key={eintrag.id}
              type="button"
              className="knopf-leise-rahmen"
              onClick={() => {
                onGewaehlt(eintrag.id, eintrag.title);
                setErgebnisse([]);
                setSuche("");
              }}
            >
              {eintrag.title}
            </button>
          ))}
        </div>
      )}
    </form>
  );
}
