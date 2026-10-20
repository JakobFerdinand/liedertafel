"use client";

import Link from "next/link";
import { type ReactNode, useEffect, useId, useState } from "react";
import { auftrittArtName } from "@/lib/events";
import {
  fetchLiedHistorie,
  type LiedHistorie as HistorieAntwort,
  type HistorieZeile,
} from "@/lib/historie";

// Wann ein Lied gesungen wurde (#160): je Auftritt eine Zeile, abgeleitet
// aus den Programmen und aus Aufnahmen des Liedes, die einen Auftritt
// nennen. Die Zahlen sind als „nach erfasster Überlieferung“ gekennzeichnet
// (keine Chronik von 120 Jahren), unsichere Daten bleiben sichtbar statt
// geglättet.

type ErweiterungsPlatz = (zeile: HistorieZeile) => ReactNode;

type LiedHistorieProps = {
  liedId: string;
  isEditor: boolean;
  // Erweiterungsplatz je Auftritt: hier hängen die Aufnahmen mit Zeitmarken
  // ein. Ohne Angabe bleibt der Platz unsichtbar — kein erfundener
  // „keine Aufnahmen“-Zustand.
  erweiterung?: ErweiterungsPlatz;
};

function anzahl(zahl: number, einzahl: string, mehrzahl: string): string {
  return `${zahl} ${zahl === 1 ? einzahl : mehrzahl}`;
}

function auftrittUrl(zeile: HistorieZeile): string {
  return `/auftritt/?id=${encodeURIComponent(zeile.eventId)}`;
}

function Zahlen({ historie }: { historie: HistorieAntwort }) {
  const { events, inProgramme, uncertainDates } = historie.counts;
  return (
    <div className="historie-zahlen">
      <p className="noten-info historie-vorbehalt">
        Die Zahlen beruhen nur auf dem, was das Archiv bisher dokumentiert hat.
        Sie sind keine vollständige Chronik der Auftritte.
      </p>
      {events === 0 ? (
        <p className="auftritt-leer">
          Für dieses Lied ist noch kein Auftritt erfasst. Das heißt nicht, dass
          es nie gesungen wurde.
        </p>
      ) : (
        <dl className="historie-summen">
          <div>
            <dt>Gesungen</dt>
            <dd>{anzahl(events, "Auftritt", "Auftritte")}</dd>
            {inProgramme < events && (
              <dd className="noten-info historie-zusatz">
                Bei {anzahl(events - inProgramme, "Auftritt", "Auftritten")}{" "}
                steht das Lied nicht im Programm, es gibt aber eine Aufnahme.
              </dd>
            )}
            {uncertainDates > 0 && (
              <dd className="noten-info historie-zusatz">
                Bei {uncertainDates} ist das Datum unsicher oder unbekannt.
              </dd>
            )}
          </div>
        </dl>
      )}
    </div>
  );
}

function Eintrag({
  zeile,
  isEditor,
  erweiterung,
}: {
  zeile: HistorieZeile;
  isEditor: boolean;
  erweiterung?: ErweiterungsPlatz;
}) {
  const platz = erweiterung?.(zeile);
  return (
    <li className="historie-eintrag" data-event-id={zeile.eventId}>
      <h4 className="historie-titel">
        <Link href={auftrittUrl(zeile)}>{zeile.eventTitle}</Link>
      </h4>
      <p className="historie-datum">
        {zeile.dateDisplay}
        {zeile.dateUncertain && (
          <span className="datum-unsicher">Datum unsicher</span>
        )}
      </p>
      <p className="nachweis-info">
        {auftrittArtName(zeile.eventKind)}
        {" · "}
        {zeile.inProgramme
          ? zeile.entries > 1
            ? `${zeile.entries}-mal im Programm`
            : "Im Programm"
          : "Nicht im Programm, aber aufgenommen"}
      </p>
      {zeile.liedAufnahmen.length > 0 && (
        <ul className="historie-aufnahmen" aria-label="Aufnahmen des Liedes">
          {zeile.liedAufnahmen.map((aufnahme) => (
            <li className="historie-aufnahme" key={aufnahme.id}>
              <span>
                Aufnahme „
                {aufnahme.description ?? aufnahme.fileName ?? "ohne Namen"}“
              </span>
            </li>
          ))}
        </ul>
      )}
      {isEditor && !zeile.eventPublished && (
        <p className="noten-info historie-hinweis">
          Auftritt noch nicht veröffentlicht, in keiner Zahl enthalten.
        </p>
      )}
      {platz && <div className="historie-erweiterung">{platz}</div>}
    </li>
  );
}

export function LiedHistorie({
  liedId,
  isEditor,
  erweiterung,
}: LiedHistorieProps) {
  const kennung = useId();
  const [seite, setSeite] = useState(1);
  const [versuch, setVersuch] = useState(0);
  const [historie, setHistorie] = useState<HistorieAntwort | null>(null);
  const [fehler, setFehler] = useState("");
  const [laedt, setLaedt] = useState(true);

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus.
    void versuch;
    setFehler("");
    setLaedt(true);
    fetchLiedHistorie(liedId, { page: seite }, abort.signal)
      .then((stand) => {
        if (abort.signal.aborted) return;
        setHistorie(stand);
        setLaedt(false);
        // Der Server klemmt eine Seite hinter dem Ende auf die letzte
        // Seite: diese Seite übernehmen statt „Seite 3 von 2“ zu zeigen.
        if (stand.page !== seite) setSeite(stand.page);
      })
      .catch((ursache) => {
        if (abort.signal.aborted) return;
        setLaedt(false);
        if (ursache instanceof Response && ursache.status === 401) {
          setFehler("Bitte erneut anmelden, um die Geschichte zu sehen.");
          return;
        }
        setFehler(
          "Die Liedgeschichte konnte nicht geladen werden. Bitte erneut versuchen.",
        );
      });
    return () => abort.abort();
  }, [liedId, seite, versuch]);

  const seitenZahl = historie
    ? Math.max(1, Math.ceil(historie.total / historie.pageSize))
    : 1;

  return (
    <section
      className="noten-bereich lied-historie"
      aria-labelledby={`${kennung}-titel`}
    >
      <h3 id={`${kennung}-titel`}>Wann gesungen</h3>
      {fehler ? (
        <div role="alert">
          <p className="feld-fehler">{fehler}</p>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => setVersuch(versuch + 1)}
          >
            Erneut versuchen
          </button>
        </div>
      ) : historie === null ? (
        <p className="auftritt-leer" aria-live="polite">
          Liedgeschichte wird geladen …
        </p>
      ) : (
        <>
          <Zahlen historie={historie} />
          <output className="visually-hidden">
            {anzahl(historie.total, "Eintrag", "Einträge")}
          </output>
          {isEditor && (historie.counts.draftEvents ?? 0) > 0 && (
            <p className="noten-info historie-zusatz">
              {anzahl(
                historie.counts.draftEvents ?? 0,
                "Auftritt ist",
                "Auftritte sind",
              )}{" "}
              noch nicht veröffentlicht und in keiner Zahl mitgezählt. Die Liste
              kann sie enthalten.
            </p>
          )}
          <div className="historie-bereich" aria-busy={laedt}>
            {laedt && (
              <output className="noten-info historie-laedt">
                Wird aktualisiert …
              </output>
            )}
            {historie.auftritte.length > 0 && (
              <ul className="historie-liste">
                {historie.auftritte.map((zeile) => (
                  <Eintrag
                    key={zeile.eventId}
                    zeile={zeile}
                    isEditor={isEditor}
                    erweiterung={erweiterung}
                  />
                ))}
              </ul>
            )}
            {seitenZahl > 1 && (
              <nav className="lieder-seiten" aria-label="Seiten der Geschichte">
                <button
                  type="button"
                  disabled={seite <= 1}
                  onClick={() => setSeite(seite - 1)}
                >
                  Zurück
                </button>
                <span className="lieder-seiten-stand">
                  Seite {historie.page} von {seitenZahl}
                </span>
                <button
                  type="button"
                  disabled={seite >= seitenZahl}
                  onClick={() => setSeite(seite + 1)}
                >
                  Weiter
                </button>
              </nav>
            )}
          </div>
        </>
      )}
    </section>
  );
}
