"use client";

import Link from "next/link";
import { type ReactNode, useEffect, useId, useState } from "react";
import { auftrittArtName } from "@/lib/events";
import {
  fetchLiedHistorie,
  type LiedHistorie as HistorieAntwort,
  type HistorieZeile,
} from "@/lib/historie";

// ARC-031: wo ein Lied dokumentiert ist. Bestätigte Aufführungen und
// unbestätigte Programmangaben stehen getrennt, die Zahlen sind als
// „nach erfasster Überlieferung“ gekennzeichnet (keine Chronik von 120
// Jahren), unsichere Daten, unbekannte Fassungen und mögliche
// Doppelerfassungen bleiben sichtbar statt geglättet.

type ErweiterungsPlatz = (zeile: HistorieZeile) => ReactNode;

type LiedHistorieProps = {
  songId: string;
  isEditor: boolean;
  // Erweiterungsplatz je Aufführung (Schlüssel: stabile Aufführungs-Id):
  // ARC-032 hängt hier Aufnahmen und Passagen ein. Ohne Angabe bleibt der
  // Platz unsichtbar — kein erfundener „keine Aufnahmen“-Zustand.
  erweiterung?: ErweiterungsPlatz;
};

function anzahl(zahl: number, einzahl: string, mehrzahl: string): string {
  return `${zahl} ${zahl === 1 ? einzahl : mehrzahl}`;
}

function auftrittUrl(zeile: HistorieZeile): string {
  return `/auftritt/?id=${encodeURIComponent(zeile.eventId)}`;
}

function fassungText(zeile: HistorieZeile): string {
  if (!zeile.arrangement) return "Fassung unbekannt";
  return zeile.musicalVersion
    ? `${zeile.arrangement.label} · ${zeile.musicalVersion.label}`
    : zeile.arrangement.label;
}

function Zahlen({
  historie,
  isEditor,
}: {
  historie: HistorieAntwort;
  isEditor: boolean;
}) {
  const { confirmed, unconfirmed, draftEventOccurrences } = historie.counts;
  const leer = confirmed.occurrences === 0 && unconfirmed.occurrences === 0;
  return (
    <div className="historie-zahlen">
      <p className="noten-info historie-vorbehalt">
        Die Zahlen beruhen nur auf dem, was das Archiv bisher dokumentiert hat.
        Sie sind keine vollständige Chronik der Aufführungen.
      </p>
      {leer ? (
        <p className="auftritt-leer">
          Für dieses Lied sind noch keine Aufführungen oder Programmangaben
          erfasst. Das heißt nicht, dass es nie gesungen wurde.
        </p>
      ) : (
        <dl className="historie-summen">
          <div>
            <dt>Bestätigt</dt>
            <dd>
              {anzahl(confirmed.occurrences, "Aufführung", "Aufführungen")} bei{" "}
              {anzahl(confirmed.events, "Auftritt", "Auftritten")}
            </dd>
            {confirmed.possiblyDuplicate > 0 && (
              <dd className="noten-info historie-zusatz">
                Davon {confirmed.possiblyDuplicate} möglicherweise doppelt
                erfasst: von Hand eingetragen neben einer Programmbestätigung am
                selben Auftritt. Die Zahl ist eine Obergrenze.
              </dd>
            )}
            {confirmed.uncertainDates > 0 && (
              <dd className="noten-info historie-zusatz">
                Bei {confirmed.uncertainDates} ist das Datum unsicher oder
                unbekannt.
              </dd>
            )}
          </div>
          <div>
            <dt>Unbestätigt</dt>
            <dd>
              {anzahl(
                unconfirmed.occurrences,
                "Programmangabe",
                "Programmangaben",
              )}{" "}
              bei {anzahl(unconfirmed.events, "Auftritt", "Auftritten")}
            </dd>
            {unconfirmed.onlyEvents > 0 && (
              <dd className="noten-info historie-zusatz">
                An {anzahl(unconfirmed.onlyEvents, "Auftritt", "Auftritten")}{" "}
                liegt nur eine Programmangabe vor, ohne bestätigte Aufführung.
              </dd>
            )}
            {unconfirmed.uncertainDates > 0 && (
              <dd className="noten-info historie-zusatz">
                Bei {unconfirmed.uncertainDates} ist das Datum unsicher oder
                unbekannt.
              </dd>
            )}
          </div>
        </dl>
      )}
      {isEditor &&
        draftEventOccurrences !== null &&
        draftEventOccurrences > 0 && (
          <p className="noten-info historie-zusatz">
            {anzahl(draftEventOccurrences, "Nachweis", "Nachweise")} an
            unveröffentlichten Auftritten sind in keiner Zahl enthalten.
          </p>
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
  const bestaetigt = zeile.evidenceStatus === "confirmed";
  const platz = erweiterung?.(zeile);
  return (
    <li className="historie-eintrag" data-performance-id={zeile.id}>
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
        <span className="nachweis-marke" data-art={zeile.evidenceStatus}>
          {bestaetigt ? "Bestätigt" : "Programmangabe"}
        </span>
        {" · "}
        {auftrittArtName(zeile.eventKind)}
        {" · "}
        {zeile.origin === "programme"
          ? "Im Programm bestätigt"
          : "Aus Archivquelle erfasst"}
        {" · "}
        {fassungText(zeile)}
      </p>
      {zeile.occurrence && zeile.occurrence.of > 1 && (
        <p className="noten-info historie-hinweis">
          Mehrmals an diesem Auftritt gesungen: Aufführung{" "}
          {zeile.occurrence.index} von {zeile.occurrence.of}.
        </p>
      )}
      {zeile.possiblyDuplicate && (
        <p className="noten-info historie-hinweis" data-art="warnung">
          Möglicherweise dieselbe Aufführung wie ein im Programm bestätigter
          Eintrag an diesem Auftritt, doppelt erfasst.
        </p>
      )}
      {zeile.alsoConfirmedAtEvent && (
        <p className="noten-info historie-hinweis">
          An diesem Auftritt ist das Lied bestätigt; die Programmangabe gehört
          vermutlich zur selben Aufführung.
        </p>
      )}
      {isEditor && !zeile.eventPublished && (
        <p className="noten-info historie-hinweis">
          Auftritt noch nicht veröffentlicht, in keiner Zahl enthalten.
        </p>
      )}
      {isEditor && zeile.sourceNote && (
        <p className="noten-info">Quellenangabe: {zeile.sourceNote}</p>
      )}
      {platz && <div className="historie-erweiterung">{platz}</div>}
    </li>
  );
}

export function LiedHistorie({
  songId,
  isEditor,
  erweiterung,
}: LiedHistorieProps) {
  const kennung = useId();
  const [seite, setSeite] = useState(1);
  const [evidenz, setEvidenz] = useState("");
  const [fassung, setFassung] = useState("");
  const [versuch, setVersuch] = useState(0);
  const [historie, setHistorie] = useState<HistorieAntwort | null>(null);
  const [fehler, setFehler] = useState("");

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus.
    void versuch;
    setFehler("");
    fetchLiedHistorie(
      songId,
      { page: seite, evidence: evidenz, arrangementId: fassung },
      abort.signal,
    )
      .then((stand) => {
        if (!abort.signal.aborted) setHistorie(stand);
      })
      .catch((ursache) => {
        if (abort.signal.aborted) return;
        if (ursache instanceof Response && ursache.status === 401) {
          setFehler("Bitte erneut anmelden, um die Geschichte zu sehen.");
          return;
        }
        setFehler(
          "Die Aufführungsgeschichte konnte nicht geladen werden. Bitte erneut versuchen.",
        );
      });
    return () => abort.abort();
  }, [songId, seite, evidenz, fassung, versuch]);

  function filterAendern(setzen: (wert: string) => void, wert: string) {
    setzen(wert);
    setSeite(1);
  }

  const seitenZahl = historie
    ? Math.max(1, Math.ceil(historie.total / historie.pageSize))
    : 1;
  const filterAktiv = evidenz !== "" || fassung !== "";
  const fassungenBekannt =
    historie !== null &&
    (historie.arrangements.length > 1 ||
      historie.unknownArrangement.confirmed +
        historie.unknownArrangement.unconfirmed >
        0);

  return (
    <section
      className="noten-bereich lied-historie"
      aria-labelledby={`${kennung}-titel`}
    >
      <h3 id={`${kennung}-titel`}>Aufführungsgeschichte</h3>
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
          Aufführungsgeschichte wird geladen …
        </p>
      ) : (
        <>
          <Zahlen historie={historie} isEditor={isEditor} />
          {(historie.total > 0 || filterAktiv) && (
            <form
              className="historie-filter"
              aria-label="Geschichte eingrenzen"
              onSubmit={(event) => event.preventDefault()}
            >
              <div>
                <label htmlFor={`${kennung}-evidenz`}>Nachweis</label>
                <select
                  id={`${kennung}-evidenz`}
                  value={evidenz}
                  onChange={(event) =>
                    filterAendern(setEvidenz, event.target.value)
                  }
                >
                  <option value="">Alle Nachweise</option>
                  <option value="confirmed">Bestätigte Aufführungen</option>
                  <option value="mention">Programmangaben</option>
                </select>
              </div>
              {(fassungenBekannt || fassung !== "") && (
                <div>
                  <label htmlFor={`${kennung}-fassung`}>Fassung</label>
                  <select
                    id={`${kennung}-fassung`}
                    value={fassung}
                    onChange={(event) =>
                      filterAendern(setFassung, event.target.value)
                    }
                  >
                    <option value="">Alle Fassungen</option>
                    {historie.arrangements.map((arrangement) => (
                      <option key={arrangement.id} value={arrangement.id}>
                        {arrangement.label} ({arrangement.confirmed} bestätigt,{" "}
                        {arrangement.unconfirmed} unbestätigt)
                      </option>
                    ))}
                    <option value="unknown">
                      Fassung unbekannt ({historie.unknownArrangement.confirmed}{" "}
                      bestätigt, {historie.unknownArrangement.unconfirmed}{" "}
                      unbestätigt)
                    </option>
                  </select>
                </div>
              )}
            </form>
          )}
          <output className="visually-hidden">
            {anzahl(historie.total, "Eintrag", "Einträge")} in dieser Auswahl
          </output>
          {historie.total === 0 && filterAktiv && (
            <p className="auftritt-leer" aria-live="polite">
              Für diese Auswahl gibt es keine Einträge.
            </p>
          )}
          {historie.performances.length > 0 && (
            <ul className="historie-liste">
              {historie.performances.map((zeile) => (
                <Eintrag
                  key={zeile.id}
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
        </>
      )}
    </section>
  );
}
