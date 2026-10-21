"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import {
  type AuftrittEntwurfEintrag,
  ladeAuftrittEntwuerfe,
  verwirfAuftrittEntwurf,
} from "@/lib/auftritt-entwuerfe";
import {
  type EntwurfEintrag,
  ladeEntwuerfe,
  verwirfEntwurf,
} from "@/lib/lied-entwuerfe";

function datumText(wert: string): string {
  return new Date(wert).toLocaleString("de-AT", {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

/**
 * "Angefangen" (#178, #131): unfertige Uploads der Redaktion (Lieder und
 * Auftritte), die einzige
 * unfertige Form. Fortsetzen öffnet das Formular mit dem gespeicherten
 * Stand, Verwerfen löscht den Eintrag; später eintreffende Lesungen ändern
 * dann nichts mehr. Wer keine Redaktion ist, sieht nichts.
 */
export function AngefangenListe() {
  const [eintraege, setEintraege] = useState<EntwurfEintrag[] | null>(null);
  const [auftritte, setAuftritte] = useState<AuftrittEntwurfEintrag[] | null>(
    null,
  );
  const [fehler, setFehler] = useState("");

  useEffect(() => {
    const steuerung = new AbortController();
    const melden = () => {
      if (!steuerung.signal.aborted) {
        setFehler("Angefangenes konnte nicht geladen werden.");
      }
    };
    ladeEntwuerfe(steuerung.signal).then(
      (liste) => setEintraege(liste),
      melden,
    );
    ladeAuftrittEntwuerfe(steuerung.signal).then(
      (liste) => setAuftritte(liste),
      melden,
    );
    return () => steuerung.abort();
  }, []);

  const auftrittVerwerfen = useCallback(async (id: string) => {
    setFehler("");
    try {
      await verwirfAuftrittEntwurf(id);
      setAuftritte((liste) => liste?.filter((e) => e.entwurfId !== id) ?? null);
    } catch {
      setFehler("Der Eintrag konnte nicht verworfen werden.");
    }
  }, []);

  const verwerfen = useCallback(async (id: string) => {
    setFehler("");
    try {
      await verwirfEntwurf(id);
      setEintraege((liste) => liste?.filter((e) => e.entwurfId !== id) ?? null);
    } catch {
      setFehler("Der Eintrag konnte nicht verworfen werden.");
    }
  }, []);

  const anzahl = (eintraege?.length ?? 0) + (auftritte?.length ?? 0);
  if (anzahl === 0 && !fehler) return null;

  return (
    <section className="angefangen" aria-labelledby="angefangen-titel">
      <h2 id="angefangen-titel">Angefangen</h2>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      <ul>
        {eintraege?.map((eintrag) => (
          <li key={eintrag.entwurfId}>
            <p>
              <strong>{eintrag.titel || "Ohne Titel"}</strong>{" "}
              <span className="feld-hinweis">
                {eintrag.dateien === 1
                  ? "1 Datei"
                  : `${eintrag.dateien} Dateien`}
                , zuletzt {datumText(eintrag.aktualisiertAm)}
              </span>
            </p>
            <div className="auth-aktionen">
              <Link
                href={`/lieder/?entwurf=${encodeURIComponent(eintrag.entwurfId)}`}
              >
                {eintrag.titel || "Ohne Titel"} fortsetzen
              </Link>
              <button
                type="button"
                className="knopf-leise"
                onClick={() => void verwerfen(eintrag.entwurfId)}
              >
                {eintrag.titel || "Ohne Titel"} verwerfen
              </button>
            </div>
          </li>
        ))}
        {auftritte?.map((eintrag) => (
          <li key={eintrag.entwurfId}>
            <p>
              <strong>{eintrag.titel || "Ohne Titel"}</strong>{" "}
              <span className="feld-hinweis">
                Auftritt,{" "}
                {eintrag.dateien === 1
                  ? "1 Datei"
                  : `${eintrag.dateien} Dateien`}
                , zuletzt {datumText(eintrag.aktualisiertAm)}
              </span>
            </p>
            <div className="auth-aktionen">
              <Link
                href={`/auftritte/?entwurf=${encodeURIComponent(eintrag.entwurfId)}`}
              >
                {eintrag.titel || "Ohne Titel"} fortsetzen
              </Link>
              <button
                type="button"
                className="knopf-leise"
                onClick={() => void auftrittVerwerfen(eintrag.entwurfId)}
              >
                {eintrag.titel || "Ohne Titel"} verwerfen
              </button>
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
