"use client";

// ARC-026: das Verzeichnis der kommenden Programme — veröffentlichte,
// aufsteigend sortiert (der Server sortiert); die Zeile zeigt Datum,
// Ort, Titel und die Liederzahl und führt in die Auftrittsansicht.
// Unbekannte und ungefähre Daten bleiben gekennzeichnet, nichts wird
// feiner gelesen, als es überliefert ist.

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { fetchMe, type MeResponse } from "@/lib/auth";
import {
  auftrittArtName,
  fetchProgramme,
  type ProgrammListeZeile,
} from "@/lib/events";

export function ProgrammListe() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [programme, setProgramme] = useState<ProgrammListeZeile[] | null>(null);
  const [fehler, setFehler] = useState("");
  const [versuch, setVersuch] = useState(0);

  const laden = useCallback(async (signal?: AbortSignal) => {
    setFehler("");
    const antwort = await fetchMe(signal);
    if (!signal?.aborted) setMe(antwort);
    if (!antwort.authenticated) return;
    try {
      const programme = await fetchProgramme(signal);
      if (!signal?.aborted) setProgramme(programme);
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 401) {
        if (!signal?.aborted) setMe({ authenticated: false });
        return;
      }
      throw ursache;
    }
  }, []);

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund
    // wird nicht nachgeladen.
    void versuch;
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden, versuch]);

  if (fehler) {
    return (
      <div aria-live="polite">
        <p className="hinweis-block">{fehler}</p>
        <p>
          <button type="button" onClick={() => setVersuch(versuch + 1)}>
            Erneut versuchen
          </button>
        </p>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p className="auth-statuszeile">Programme werden geladen …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um die Programme zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }

  return (
    <section aria-labelledby="programme-verzeichnis-titel">
      <h2 id="programme-verzeichnis-titel">Kommende Programme</h2>
      {programme === null ? (
        <p aria-live="polite" className="auth-statuszeile">
          Programme werden geladen …
        </p>
      ) : programme.length === 0 ? (
        <p className="hinweis-block">
          Es steht derzeit kein veröffentlichtes Programm an.
        </p>
      ) : (
        <ul className="programme-einträge">
          {programme.map((programm) => {
            // Ehrliche Unsicherheit wie im Auftrittsverzeichnis: nur das
            // genaue Tagesdatum gilt als sicher.
            const unsicher =
              programm.dateApproximate || programm.datePrecision !== "day";
            return (
              <li className="programme-eintrag" key={programm.eventId}>
                <p
                  className="auftritt-datum"
                  data-unbestimmt={
                    programm.datePrecision === "unknown" ? "true" : undefined
                  }
                >
                  {programm.dateDisplay}
                  {unsicher && (
                    <span className="datum-unsicher">Datum unsicher</span>
                  )}
                </p>
                <div className="auftritte-inhalt">
                  <p className="auftritte-art">
                    {auftrittArtName(programm.kind)}
                  </p>
                  <h3>
                    <Link
                      href={`/auftritt/?id=${encodeURIComponent(programm.eventId)}`}
                    >
                      {programm.eventTitle}
                    </Link>
                  </h3>
                  {programm.venue && (
                    <p className="auftritt-ort">{programm.venue}</p>
                  )}
                  {programm.startTime && (
                    <p className="auftritt-zeit">{programm.startTime} Uhr</p>
                  )}
                  <p className="noten-info">
                    {programm.itemCount}{" "}
                    {programm.itemCount === 1 ? "Lied" : "Lieder"}
                  </p>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
