"use client";

// ARC-029: tatsächlich gesungenes Programm. Der Plan (Abschnitt Programm)
// und die Aufführung bleiben getrennt: Mitglieder lesen unter dem Plan,
// was nach der Bestätigung wirklich gesungen wurde — samt abweichender
// Fassung, Zugaben und ausgefallenen Programmpunkten — oder, solange
// nichts bestätigt ist, ehrlich „noch nicht bestätigt“. Die Redaktion
// bestätigt eine veröffentlichte Revision an einer Werkbank: je
// Programmpunkt „gesungen“ oder „nicht gesungen“ (ausdrücklich, nichts
// wird stillschweigend angenommen), optional eine andere Fassung, dazu
// zusätzlich gesungene Lieder. „Programm unverändert bestätigen“ ist die
// Ein-Klick-Aussage für den häufigen Fall. Das Absenden ist eine ganze,
// wiederholbare Aussage (PUT): dieselben Nachweise werden fortgeschrieben,
// ein veralteter Stand lädt den frischen und bittet um Nacharbeit.

import Link from "next/link";
import { useCallback, useEffect, useRef, useState } from "react";
import { LiedWahl } from "@/components/auftritt-programm";
import { FassungsWahl } from "@/components/fassungs-wahl";
import { problemTitel } from "@/lib/assets";
import {
  type AuftrittDetailsMitProgramm,
  type BestaetigungAbsenden,
  type BestaetigungFassung,
  type BestaetigungNachweis,
  type BestaetigungPruefung,
  bestaetigungUrl,
  fetchBestaetigung,
  publishedAtText,
  putBestaetigung,
} from "@/lib/events";
import { fetchSong, type LiedDetails } from "@/lib/songs";
import { ZeitmarkenVorhanden } from "@/lib/zeitmarken";

const ersatzFehler = "Das hat nicht geklappt. Bitte erneut versuchen.";

/**
 * Warnung vor einer Auslassung oder Entfernung (ARC-032): Zeitmarken in
 * Aufnahmen halten die Aufführung fest, bis sie dort entfernt sind.
 */
function zeitmarkenWarnung(
  zahl: number,
  folge: "ausgelassen" | "entfernt",
): string {
  const haengt = zahl === 1 ? "hängt 1 Zeitmarke" : `hängen ${zahl} Zeitmarken`;
  const marken = zahl === 1 ? "die Zeitmarke" : "die Zeitmarken";
  const ist = zahl === 1 ? "ist" : "sind";
  return `An diesem Lied ${haengt} in Aufnahmen. Es kann erst ${folge} werden, wenn ${marken} in den Aufnahmen entfernt ${ist}.`;
}

const veralteterStand =
  "Die Bestätigung wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

const unentschieden =
  "Bitte gib für jeden Programmpunkt an, ob er gesungen wurde.";

const liedLadeFehler =
  "Ein Lied konnte nicht geladen werden. Die Fassungsauswahl bleibt leer.";

type Ausgang = "sung" | "skipped";

// Zeile der Werkbank für ein zusätzlich gesungenes Lied: vorhandene
// Nachweise tragen ihre Id und Marke, neue einen lokalen Client-Schlüssel
// (derselbe Schlüssel bei jedem Wiederholungsversuch).
type ZusatzZeile = {
  schluessel: string;
  performanceId: string | null;
  rowVersion: number | null;
  clientKey: string;
  songId: string;
  titel: string | null;
  arrangementId: string | null;
  musicalVersionId: string | null;
};

type Korrektur = { arrangementId: string; versionId: string };

/** Meta-Leiter der Fassung: Satz, Fassung, Tonart und Stimmkonfiguration,
    Unbekanntes bleibt weg (nichts wird erfunden). */
function fassungsTeile(fassung: BestaetigungFassung): string[] {
  return [
    fassung.arrangementLabel,
    fassung.musicalVersionLabel,
    fassung.musicalKey ? `Tonart: ${fassung.musicalKey}` : null,
    fassung.voiceConfiguration
      ? `Stimmkonfiguration: ${fassung.voiceConfiguration}`
      : null,
  ].filter((teil): teil is string => teil !== null);
}

function zeilenAusPruefung(pruefung: BestaetigungPruefung): {
  ausgang: Record<string, Ausgang | null>;
  korrektur: Record<string, Korrektur | null>;
  zusaetze: ZusatzZeile[];
} {
  const ausgang: Record<string, Ausgang | null> = {};
  const korrektur: Record<string, Korrektur | null> = {};
  for (const punkt of pruefung.items) {
    if (punkt.outcome === "skipped") ausgang[punkt.programmeItemId] = "skipped";
    else if (punkt.outcome === "sung" || punkt.outcome === "unconfirmed")
      ausgang[punkt.programmeItemId] = "sung";
    // Offene Punkte mit übernehmbarem früherem Nachweis (nach einer
    // Neuveröffentlichung) starten als „gesungen“; alles andere bleibt
    // ausdrücklich unentschieden.
    else
      ausgang[punkt.programmeItemId] = punkt.suggestedPerformanceId
        ? "sung"
        : null;
    // Die gesungene Fassung folgt dem vorhandenen oder dem zur Übernahme
    // vorgeschlagenen Nachweis, damit eine Übernahme sie behält.
    const gesungen = punkt.performance ?? punkt.suggestedPerformance;
    korrektur[punkt.programmeItemId] =
      gesungen?.musicalVersionId &&
      gesungen.arrangementId &&
      gesungen.musicalVersionId !== punkt.musicalVersionId
        ? {
            arrangementId: gesungen.arrangementId,
            versionId: gesungen.musicalVersionId,
          }
        : null;
  }
  return {
    ausgang,
    korrektur,
    zusaetze: pruefung.additions.map((nachweis) => ({
      schluessel: nachweis.id,
      performanceId: nachweis.id,
      rowVersion: nachweis.rowVersion,
      clientKey: crypto.randomUUID(),
      songId: nachweis.songId,
      titel: nachweis.songTitle,
      arrangementId: nachweis.arrangementId,
      musicalVersionId: nachweis.musicalVersionId,
    })),
  };
}

/** Aussage der Werkbank: jeder Programmpunkt ausdrücklich entschieden. */
function absendeKoerper(
  pruefung: BestaetigungPruefung,
  ausgang: Record<string, Ausgang | null>,
  korrektur: Record<string, Korrektur | null>,
  zusaetze: ZusatzZeile[],
): BestaetigungAbsenden {
  return {
    revisionId: pruefung.revision.id,
    rowVersion: pruefung.rowVersion,
    items: pruefung.items.map((punkt) => {
      const wert = ausgang[punkt.programmeItemId] ?? "skipped";
      const eigener = punkt.performance ?? punkt.suggestedPerformance;
      const vorhanden = eigener?.id ?? null;
      const geaendert = korrektur[punkt.programmeItemId];
      return {
        programmeItemId: punkt.programmeItemId,
        outcome: wert,
        ...(wert === "sung" && vorhanden ? { performanceId: vorhanden } : {}),
        ...(wert === "sung" && eigener
          ? { rowVersion: eigener.rowVersion }
          : {}),
        ...(wert === "sung" && geaendert
          ? { musicalVersionId: geaendert.versionId }
          : {}),
      };
    }),
    knownOccurrences: [
      ...pruefung.items.flatMap((punkt) =>
        punkt.performance
          ? [
              {
                performanceId: punkt.performance.id,
                rowVersion: punkt.performance.rowVersion,
              },
            ]
          : [],
      ),
      ...pruefung.additions.map((nachweis) => ({
        performanceId: nachweis.id,
        rowVersion: nachweis.rowVersion,
      })),
    ],
    additions: zusaetze.map((zeile) => ({
      ...(zeile.performanceId
        ? {
            performanceId: zeile.performanceId,
            ...(zeile.rowVersion !== null
              ? { rowVersion: zeile.rowVersion }
              : {}),
          }
        : { clientKey: zeile.clientKey }),
      songId: zeile.songId,
      ...(zeile.musicalVersionId
        ? { musicalVersionId: zeile.musicalVersionId }
        : {}),
    })),
  };
}

/** Vergleichsstand der Werkbank: ändert sich etwas, gilt sie als bearbeitet. */
function standVon(
  ausgang: Record<string, Ausgang | null>,
  korrektur: Record<string, Korrektur | null>,
  zusaetze: ZusatzZeile[],
): string {
  return JSON.stringify([
    ausgang,
    korrektur,
    zusaetze.map((z) => [z.schluessel, z.songId, z.musicalVersionId]),
  ]);
}

/** Ids der Nachweise, die der Körper behält (übernimmt oder fortschreibt). */
function behalteneIds(koerper: BestaetigungAbsenden): Set<string> {
  return new Set(
    [...koerper.items, ...koerper.additions]
      .map((eintrag) => eintrag.performanceId)
      .filter((id): id is string => id !== undefined),
  );
}

/** Alle Nachweise, die die Prüfansicht der Bestätigung zuordnet. */
function bekannteNachweise(
  pruefung: BestaetigungPruefung,
): BestaetigungNachweis[] {
  return [
    ...pruefung.items.flatMap((punkt) =>
      punkt.performance ? [punkt.performance] : [],
    ),
    ...pruefung.additions,
  ];
}

// ─── Lesesaal ────────────────────────────────────────────────────────

function Lesesaal({
  bestaetigung,
}: {
  bestaetigung: NonNullable<
    NonNullable<AuftrittDetailsMitProgramm["programme"]>["confirmation"]
  >;
}) {
  const nichts = bestaetigung.actual.length === 0;
  return (
    <>
      <p className="programm-stand">
        Bestätigt am {publishedAtText(bestaetigung.confirmedAt)} · Grundlage:
        Revision {bestaetigung.revisionNumber} des Programms
      </p>
      {!bestaetigung.upToDate && (
        <p className="noten-info">
          Das Programm wurde nach dieser Bestätigung neu veröffentlicht; sie
          bezieht sich auf die frühere Revision {bestaetigung.revisionNumber}.
        </p>
      )}
      {nichts ? (
        <p className="auftritt-leer">
          Laut Bestätigung wurde keines der Lieder gesungen.
        </p>
      ) : (
        <ol className="programm-liste bestaetigung-ist">
          {bestaetigung.actual.map((eintrag, index) => {
            const marken = [
              eintrag.added ? "Zusätzlich gesungen" : null,
              eintrag.differsFromPlan
                ? `Andere Fassung als geplant${eintrag.plannedMusicalVersionLabel ? ` (geplant: ${eintrag.plannedMusicalVersionLabel})` : ""}`
                : null,
              eintrag.evidenceStatus === "mention"
                ? "Programmangabe, nicht bestätigt"
                : null,
              ...fassungsTeile(eintrag),
            ].filter((teil): teil is string => teil !== null);
            return (
              <li className="programm-eintrag" key={eintrag.performanceId}>
                <h4 className="programm-titel">
                  <span className="programm-nummer" aria-hidden="true">
                    {index + 1}.
                  </span>{" "}
                  <Link href={bestaetigungUrl(eintrag)}>
                    {eintrag.songTitle ?? "Ohne Titel"}
                  </Link>
                </h4>
                {marken.length > 0 && (
                  <p className="noten-info">{marken.join(" · ")}</p>
                )}
              </li>
            );
          })}
        </ol>
      )}
      {bestaetigung.skipped.length > 0 && (
        <div className="bestaetigung-ausgefallen">
          <p className="programm-fruehere-einleitung">
            Geplant, aber nicht gesungen
          </p>
          <ul className="bestaetigung-ausgefallen-liste">
            {bestaetigung.skipped.map((punkt) => (
              <li key={punkt.programmeItemId}>
                <span className="programm-nummer" aria-hidden="true">
                  {punkt.position}.
                </span>{" "}
                {punkt.songTitle ?? "Ohne Titel"}
              </li>
            ))}
          </ul>
        </div>
      )}
    </>
  );
}

// ─── Werkbank der Redaktion ──────────────────────────────────────────

function Werkbank({
  eventId,
  aktualisieren,
}: {
  eventId: string;
  aktualisieren: () => void;
}) {
  const [ausgeklappt, setAusgeklappt] = useState(false);
  const [pruefung, setPruefung] = useState<BestaetigungPruefung | null>(null);
  const [ladeFehler, setLadeFehler] = useState("");
  const [ausgang, setAusgang] = useState<Record<string, Ausgang | null>>({});
  const [korrektur, setKorrektur] = useState<Record<string, Korrektur | null>>(
    {},
  );
  const [zusaetze, setZusaetze] = useState<ZusatzZeile[]>([]);
  const [lieder, setLieder] = useState<Record<string, LiedDetails | "fehler">>(
    {},
  );
  const [fassungOffen, setFassungOffen] = useState<string | null>(null);
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [busy, setBusy] = useState(false);
  // Vergleichsstand nach dem Laden/Speichern (ungespeicherte Arbeit) und die
  // Freigabe eines Entfernens von Quellenangaben (zweiter Klick).
  const [grundstand, setGrundstand] = useState("");
  const [verlustFreigabe, setVerlustFreigabe] = useState("");
  const meldungRef = useRef<HTMLDivElement>(null);

  // Nach einem Speichern oder einem Konflikt führt der Fokus zur Meldung.
  useEffect(() => {
    if (erfolg || hinweis) meldungRef.current?.focus();
  }, [erfolg, hinweis]);

  const uebernehmen = useCallback((neu: BestaetigungPruefung) => {
    const zeilen = zeilenAusPruefung(neu);
    setGrundstand(standVon(zeilen.ausgang, zeilen.korrektur, zeilen.zusaetze));
    setVerlustFreigabe("");
    setPruefung(neu);
    setAusgang(zeilen.ausgang);
    setKorrektur(zeilen.korrektur);
    setZusaetze(zeilen.zusaetze);
    setFassungOffen(null);
  }, []);

  // Beim Aufklappen kommt die Prüfansicht frisch vom Server; die Werkbank
  // hält sonst keinen eigenen Stand vor.
  useEffect(() => {
    if (!ausgeklappt) return;
    const abbruch = new AbortController();
    setLadeFehler("");
    fetchBestaetigung(eventId, undefined, abbruch.signal)
      .then((neu) => {
        if (!abbruch.signal.aborted) uebernehmen(neu);
      })
      .catch(async (ursache) => {
        if (abbruch.signal.aborted) return;
        setLadeFehler(await problemTitel(ursache, ersatzFehler));
      });
    return () => abbruch.abort();
  }, [ausgeklappt, eventId, uebernehmen]);

  // Lieddetails für die Fassungswahl werden erst bei Bedarf geholt.
  async function liedLaden(songId: string) {
    if (songId in lieder) return;
    try {
      const lied = await fetchSong(songId);
      setLieder((bisher) => ({ ...bisher, [songId]: lied }));
    } catch {
      setLieder((bisher) => ({ ...bisher, [songId]: "fehler" }));
    }
  }

  function fassungUmschalten(schluessel: string, songId: string) {
    if (fassungOffen === schluessel) {
      setFassungOffen(null);
      return;
    }
    setFassungOffen(schluessel);
    void liedLaden(songId);
  }

  function neuerZusatz(songId: string) {
    const schluessel = crypto.randomUUID();
    setZusaetze((bisher) => [
      ...bisher,
      {
        schluessel,
        performanceId: null,
        rowVersion: null,
        clientKey: crypto.randomUUID(),
        songId,
        titel: null,
        arrangementId: null,
        musicalVersionId: null,
      },
    ]);
    void liedLaden(songId);
  }

  async function absenden(koerper: BestaetigungAbsenden) {
    setBusy(true);
    setHinweis("");
    setErfolg("");
    try {
      const neu = await putBestaetigung(eventId, koerper);
      uebernehmen(neu);
      setErfolg("Bestätigung gespeichert.");
      aktualisieren();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        const titel = await problemTitel(ursache, veralteterStand);
        if (titel.startsWith(ZeitmarkenVorhanden)) {
          // Kein veralteter Stand, sondern eine Sperre dieses Zustands:
          // Zeitmarken in Aufnahmen hängen an der Aufführung. Die Eingaben
          // bleiben stehen; erst die Marken entfernen, dann erneut speichern.
          setHinweis(titel);
          return;
        }
        let frisch: BestaetigungPruefung | null = null;
        try {
          frisch = await fetchBestaetigung(eventId);
        } catch {
          // Der Hinweis steht; das Nachladen wiederholt sich beim Aufklappen.
        }
        if (frisch && !frisch.confirmable) {
          // Kein Konflikt, sondern eine Sperre: die Eingaben bleiben stehen,
          // nur die Bestätigung ist (noch) nicht möglich.
          const grund = frisch.unconfirmableReason;
          setPruefung((bisher) =>
            bisher
              ? { ...bisher, confirmable: false, unconfirmableReason: grund }
              : bisher,
          );
          setHinweis(titel);
          return;
        }
        // Veralteter Stand (oder neu veröffentlicht): der frische Stand
        // kommt sofort, die Redaktion prüft und wiederholt die Änderung.
        setHinweis(
          titel === veralteterStand
            ? titel
            : `${titel} Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.`,
        );
        if (frisch) uebernehmen(frisch);
        aktualisieren();
        return;
      }
      setHinweis(await problemTitel(ursache, ersatzFehler));
    } finally {
      setBusy(false);
    }
  }

  function speichern() {
    if (!pruefung) return;
    if (pruefung.items.some((punkt) => !ausgang[punkt.programmeItemId])) {
      setHinweis(unentschieden);
      return;
    }
    const koerper = absendeKoerper(pruefung, ausgang, korrektur, zusaetze);
    // Wer einen Nachweis mit Quellenangabe abwählt oder entfernt, verliert
    // die Angabe: erst ein zweiter Klick auf dieselbe Aussage gibt frei.
    const behalten = behalteneIds(koerper);
    const verlust = bekannteNachweise(pruefung).filter(
      (nachweis) => nachweis.sourceNote?.trim() && !behalten.has(nachweis.id),
    );
    if (verlust.length > 0) {
      const marke = verlust.map((nachweis) => nachweis.id).join(",");
      if (verlustFreigabe !== marke) {
        setVerlustFreigabe(marke);
        setHinweis(
          `Beim Speichern werden Nachweise samt Quellenangabe entfernt: ${verlust.map((nachweis) => nachweis.songTitle ?? "Ohne Titel").join(", ")}. Zum Bestätigen noch einmal „Bestätigung speichern“ wählen.`,
        );
        return;
      }
    }
    void absenden(koerper);
  }

  function unveraendertBestaetigen() {
    if (!pruefung) return;
    const alles: Record<string, Ausgang | null> = {};
    for (const punkt of pruefung.items) alles[punkt.programmeItemId] = "sung";
    void absenden(absendeKoerper(pruefung, alles, {}, []));
  }

  const dirty =
    pruefung !== null && standVon(ausgang, korrektur, zusaetze) !== grundstand;
  const gesperrt = pruefung !== null && !pruefung.confirmable;
  // Hand erfasste Nachweise, die der Stand der Werkbank nicht übernimmt:
  // sie zählten neben der Bestätigung ein zweites Mal.
  const unadoptiert = pruefung
    ? pruefung.unownedOccurrences.filter(
        (nachweis) =>
          !behalteneIds(
            absendeKoerper(pruefung, ausgang, korrektur, zusaetze),
          ).has(nachweis.id),
      )
    : [];

  // ARC-032: Zeitmarken je Zugabe (nach Zeilenschlüssel) und Zugaben, die
  // entfernt sind, obwohl Zeitmarken an ihnen hängen.
  const zusatzMarken = new Map(
    (pruefung?.additions ?? []).map((nachweis) => [
      nachweis.id,
      nachweis.passageCount ?? 0,
    ]),
  );
  const entfernteMitMarken = (pruefung?.additions ?? []).filter(
    (nachweis) =>
      (nachweis.passageCount ?? 0) > 0 &&
      !zusaetze.some((zeile) => zeile.performanceId === nachweis.id),
  );

  const lied = (songId: string) => {
    const stand = lieder[songId];
    return stand && stand !== "fehler" ? stand : null;
  };

  return (
    <details
      className="programm-verwaltung bestaetigung-verwaltung"
      onToggle={(event) =>
        setAusgeklappt((event.target as HTMLDetailsElement).open)
      }
    >
      <summary>
        <h4 id="bestaetigung-verwaltung-titel">Aufführung bestätigen</h4>
        <span className="programm-verwaltung-umschalter" aria-hidden="true">
          <span className="programm-verwaltung-auf">Ausklappen</span>
          <span className="programm-verwaltung-zu">Einklappen</span>
        </span>
      </summary>
      <fieldset
        className="programm-verwaltung"
        aria-label="Aufführung bestätigen"
        disabled={gesperrt}
      >
        {ausgeklappt && (
          <>
            {/* Dauerhafte Meldungsfläche: die Bereiche sind immer eingehängt,
                damit Hilfstechnik Änderungen zuverlässig ansagt; nach einem
                Speichern oder Konflikt führt der Fokus hierher. */}
            <div
              ref={meldungRef}
              tabIndex={-1}
              className="bestaetigung-meldung"
            >
              <output className={erfolg ? "auth-erfolg" : undefined}>
                {erfolg}
              </output>
              <div
                role="alert"
                className={hinweis || ladeFehler ? "feld-fehler" : undefined}
              >
                {hinweis || ladeFehler}
              </div>
            </div>
            {!pruefung && !ladeFehler && (
              <p className="noten-info">Prüfansicht wird geladen …</p>
            )}
            {gesperrt && pruefung?.unconfirmableReason && (
              <p className="noten-info bestaetigung-gesperrt">
                {pruefung.unconfirmableReason} Bestätigen lässt sich das
                Programm erst nach dem Auftritt.
              </p>
            )}
            {pruefung && (
              <>
                <p className="programm-stand">
                  {pruefung.confirmation
                    ? `Bestätigt: Revision ${pruefung.confirmation.revisionNumber ?? "?"} · zuletzt am ${publishedAtText(pruefung.confirmation.updatedAt)}`
                    : "Noch nicht bestätigt."}
                </p>
                <p className="programm-stand">
                  {`Prüfung gegen Revision ${pruefung.revision.number} · veröffentlicht am ${publishedAtText(pruefung.revision.publishedAt)}`}
                </p>
                <p className="noten-info">
                  Die Bestätigung ändert den Plan nicht. Sie hält fest, was
                  tatsächlich gesungen wurde; nur bestätigte Lieder zählen für
                  die Liedgeschichte.
                </p>
                <ul className="programm-zeilen">
                  {pruefung.items.map((punkt) => {
                    const wert = ausgang[punkt.programmeItemId] ?? null;
                    const gewaehlt = korrektur[punkt.programmeItemId] ?? null;
                    const offen = fassungOffen === punkt.programmeItemId;
                    const liedStand = lied(punkt.songId);
                    const gewaehlteFassung = gewaehlt
                      ? liedStand?.arrangements
                          .find((a) => a.id === gewaehlt.arrangementId)
                          ?.musicalVersions.find(
                            (v) => v.id === gewaehlt.versionId,
                          )?.label
                      : null;
                    return (
                      <li
                        className="programm-zeile bestaetigung-zeile"
                        key={punkt.programmeItemId}
                      >
                        <div className="programm-zeile-kopf">
                          <span
                            className="programm-zeile-nummer"
                            aria-hidden="true"
                          >
                            {punkt.position}.
                          </span>
                          <span className="programm-zeile-titel">
                            {punkt.songTitle ?? "Ohne Titel"}
                          </span>
                        </div>
                        <p className="noten-info">
                          Geplant: {fassungsTeile(punkt).join(" · ") || "—"}
                          {punkt.outcome === "unconfirmed" &&
                            " · Nachweis ist inzwischen nur noch eine Programmangabe"}
                        </p>
                        <fieldset className="belege-art bestaetigung-ausgang">
                          <legend>
                            {`Programmpunkt ${punkt.position}: ${punkt.songTitle ?? "Ohne Titel"}`}
                          </legend>
                          <div className="belege-art-auswahl">
                            <label>
                              <input
                                type="radio"
                                name={`bestaetigung-${punkt.programmeItemId}`}
                                checked={wert === "sung"}
                                onChange={() =>
                                  setAusgang((bisher) => ({
                                    ...bisher,
                                    [punkt.programmeItemId]: "sung",
                                  }))
                                }
                              />
                              Gesungen
                            </label>
                            <label>
                              <input
                                type="radio"
                                name={`bestaetigung-${punkt.programmeItemId}`}
                                checked={wert === "skipped"}
                                onChange={() =>
                                  setAusgang((bisher) => ({
                                    ...bisher,
                                    [punkt.programmeItemId]: "skipped",
                                  }))
                                }
                              />
                              Nicht gesungen
                            </label>
                          </div>
                        </fieldset>
                        {wert === "sung" && punkt.suggestedPerformance && (
                          <p className="noten-info">
                            Ein vorhandener Nachweis dieses Liedes wird
                            übernommen und behält seine Kennung
                            {punkt.suggestedPerformance.sourceNote
                              ? ` und die Quellenangabe: ${punkt.suggestedPerformance.sourceNote}`
                              : ""}
                            .
                          </p>
                        )}
                        {wert === "skipped" &&
                          (punkt.performance?.passageCount ?? 0) > 0 && (
                            <p className="feld-fehler">
                              {zeitmarkenWarnung(
                                punkt.performance?.passageCount ?? 0,
                                "ausgelassen",
                              )}
                            </p>
                          )}
                        {wert === "skipped" &&
                          punkt.performance?.sourceNote?.trim() && (
                            <p className="feld-fehler">
                              Der vorhandene Nachweis hat eine Quellenangabe (
                              {punkt.performance.sourceNote}); sie geht beim
                              Speichern verloren.
                            </p>
                          )}
                        {wert === "sung" && (
                          <div className="bestaetigung-fassung">
                            <button
                              type="button"
                              className="knopf-leise"
                              aria-expanded={offen}
                              aria-label={`${offen ? "Fassungswahl schließen" : "Andere Fassung gesungen"}: ${punkt.songTitle ?? "Ohne Titel"}`}
                              onClick={() =>
                                fassungUmschalten(
                                  punkt.programmeItemId,
                                  punkt.songId,
                                )
                              }
                            >
                              {offen
                                ? "Fassungswahl schließen"
                                : "Andere Fassung gesungen"}
                            </button>
                            {gewaehlt && (
                              <>
                                <span className="noten-info">
                                  Gesungen:{" "}
                                  {gewaehlteFassung ?? "andere Fassung"}
                                </span>
                                <button
                                  type="button"
                                  className="knopf-leise"
                                  aria-label={`Wie geplant: ${punkt.songTitle ?? "Ohne Titel"}`}
                                  onClick={() =>
                                    setKorrektur((bisher) => ({
                                      ...bisher,
                                      [punkt.programmeItemId]: null,
                                    }))
                                  }
                                >
                                  Wie geplant
                                </button>
                              </>
                            )}
                            {offen &&
                              (liedStand ? (
                                <FassungsWahl
                                  lied={liedStand}
                                  arrangementId={gewaehlt?.arrangementId ?? ""}
                                  versionId={gewaehlt?.versionId ?? ""}
                                  onSelect={(arrangementId, versionId) =>
                                    setKorrektur((bisher) => ({
                                      ...bisher,
                                      [punkt.programmeItemId]:
                                        versionId === punkt.musicalVersionId
                                          ? null
                                          : { arrangementId, versionId },
                                    }))
                                  }
                                />
                              ) : (
                                <p className="feld-hinweis">
                                  {lieder[punkt.songId] === "fehler"
                                    ? liedLadeFehler
                                    : "Lied wird geladen …"}
                                </p>
                              ))}
                          </div>
                        )}
                      </li>
                    );
                  })}
                </ul>

                <p className="programm-fruehere-einleitung">
                  Zusätzlich gesungen (Zugaben)
                </p>
                {entfernteMitMarken.map((nachweis) => (
                  <p className="feld-fehler" key={nachweis.id}>
                    {`Zugabe „${nachweis.songTitle ?? "Ohne Titel"}“ ist entfernt, aber nicht gespeichert. ${zeitmarkenWarnung(nachweis.passageCount, "entfernt")}`}
                  </p>
                ))}
                {zusaetze.length === 0 ? (
                  <p className="auftritt-leer">
                    Keine zusätzlichen Lieder erfasst.
                  </p>
                ) : (
                  <ul className="programm-zeilen">
                    {zusaetze.map((zeile) => {
                      const liedStand = lied(zeile.songId);
                      const offen = fassungOffen === zeile.schluessel;
                      const titel = liedStand?.title ?? zeile.titel;
                      return (
                        <li
                          className="programm-zeile bestaetigung-zusatz"
                          key={zeile.schluessel}
                        >
                          <div className="programm-zeile-kopf">
                            <span className="programm-zeile-titel">
                              {titel ?? "Lied wird geladen …"}
                            </span>
                          </div>
                          <div className="programm-zeile-werkzeuge">
                            <button
                              type="button"
                              className="knopf-leise"
                              aria-expanded={offen}
                              aria-label={`${offen ? "Fassungswahl schließen" : "Fassung wählen"}: ${titel ?? "Zusätzliches Lied"}`}
                              onClick={() =>
                                fassungUmschalten(
                                  zeile.schluessel,
                                  zeile.songId,
                                )
                              }
                            >
                              {offen
                                ? "Fassungswahl schließen"
                                : "Fassung wählen"}
                            </button>
                            <button
                              type="button"
                              className="knopf-leise"
                              aria-label={`${titel ?? "Zusätzliches Lied"} entfernen`}
                              onClick={() =>
                                setZusaetze((bisher) =>
                                  bisher.filter(
                                    (andere) =>
                                      andere.schluessel !== zeile.schluessel,
                                  ),
                                )
                              }
                            >
                              Entfernen
                            </button>
                          </div>
                          {(zusatzMarken.get(zeile.schluessel) ?? 0) > 0 && (
                            <p className="feld-fehler">
                              {zeitmarkenWarnung(
                                zusatzMarken.get(zeile.schluessel) ?? 0,
                                "entfernt",
                              )}
                            </p>
                          )}
                          <p className="noten-info">
                            {zeile.musicalVersionId
                              ? `Fassung: ${liedStand?.arrangements.flatMap((a) => a.musicalVersions).find((v) => v.id === zeile.musicalVersionId)?.label ?? "gewählt"}`
                              : "Fassung unbekannt"}
                          </p>
                          {offen &&
                            (liedStand ? (
                              <FassungsWahl
                                lied={liedStand}
                                arrangementId={zeile.arrangementId ?? ""}
                                versionId={zeile.musicalVersionId ?? ""}
                                onSelect={(arrangementId, versionId) =>
                                  setZusaetze((bisher) =>
                                    bisher.map((andere) =>
                                      andere.schluessel === zeile.schluessel
                                        ? {
                                            ...andere,
                                            arrangementId,
                                            musicalVersionId: versionId,
                                          }
                                        : andere,
                                    ),
                                  )
                                }
                              />
                            ) : (
                              <p className="feld-hinweis">
                                {lieder[zeile.songId] === "fehler"
                                  ? liedLadeFehler
                                  : "Lied wird geladen …"}
                              </p>
                            ))}
                        </li>
                      );
                    })}
                  </ul>
                )}
                <LiedWahl
                  kennung="bestaetigung-lied-suche"
                  beschriftung="Zusätzlich gesungenes Lied suchen"
                  onGewaehlt={neuerZusatz}
                />

                {unadoptiert.length > 0 && (
                  <p className="feld-fehler bestaetigung-doppelt">
                    Zu diesem Auftritt gibt es bereits von Hand erfasste
                    Nachweise, die nicht übernommen werden:{" "}
                    {unadoptiert
                      .map(
                        (nachweis) =>
                          `${nachweis.songTitle ?? "Ohne Titel"} (${nachweis.evidenceStatus === "confirmed" ? "bestätigt" : "Programmangabe"})`,
                      )
                      .join(", ")}
                    . Die Lieder zählten sonst doppelt.
                  </p>
                )}
                {dirty && pruefung.confirmation === null && (
                  <p className="noten-info bestaetigung-ungespeichert">
                    Es gibt nicht gespeicherte Änderungen in der Werkbank.
                    „Programm unverändert bestätigen“ würde sie verwerfen:
                    speichere sie mit „Bestätigung speichern“ oder lade die
                    Ansicht neu.
                  </p>
                )}
                <div className="noten-aktionen">
                  {pruefung.confirmation === null && (
                    <button
                      type="button"
                      disabled={busy || gesperrt || dirty}
                      onClick={unveraendertBestaetigen}
                    >
                      {busy
                        ? "Wird gespeichert …"
                        : "Programm unverändert bestätigen"}
                    </button>
                  )}
                  <button
                    type="button"
                    disabled={busy || gesperrt}
                    onClick={speichern}
                  >
                    {busy ? "Wird gespeichert …" : "Bestätigung speichern"}
                  </button>
                </div>
              </>
            )}
          </>
        )}
      </fieldset>
    </details>
  );
}

// ─── Der Bereich ─────────────────────────────────────────────────────

export function AuftrittBestaetigung({
  auftritt,
  isEditor,
  aktualisieren,
}: {
  auftritt: AuftrittDetailsMitProgramm;
  isEditor: boolean;
  aktualisieren: () => void;
}) {
  const programm = auftritt.programme ?? null;
  // Ohne veröffentlichten Plan gibt es nichts zu bestätigen; der Abschnitt
  // Programm erklärt diesen Stand bereits.
  if (!programm?.published) return null;
  const bestaetigung = programm.confirmation ?? null;
  return (
    <section
      className="auftritt-abschnitt auftritt-bestaetigung"
      aria-labelledby="auftritt-bestaetigung-titel"
    >
      <h3 id="auftritt-bestaetigung-titel">Tatsächlich gesungen</h3>
      {bestaetigung ? (
        <Lesesaal bestaetigung={bestaetigung} />
      ) : (
        <p className="auftritt-leer">
          Noch nicht bestätigt. Das Programm oben ist der Plan; welche Lieder
          tatsächlich gesungen wurden, steht hier, sobald die Redaktion es
          bestätigt hat.
        </p>
      )}
      {isEditor && (
        <Werkbank eventId={auftritt.id} aktualisieren={aktualisieren} />
      )}
    </section>
  );
}
