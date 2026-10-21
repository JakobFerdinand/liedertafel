"use client";

// Programm des Auftritts (#160): ein Programm je Auftritt, geordnete
// Programmpunkte mit tieflinkender Liedseite für alle Mitglieder. Die
// Redaktion bearbeitet dieselbe Liste in der Werkbank: je Eintrag eine
// Zeile mit Lied, Notiz, Umordnen (↑/↓) und Entfernen. „Programm
// speichern“ (PUT, ganzer geordneter Ersatz) veröffentlicht sofort —
// es gibt weder Entwurf noch Revisionen noch eine Bestätigung; was hier
// steht, gilt als gesungen und wird nachträglich korrigiert, wenn es
// anders kam. Veraltete Stände laden den frischen Stand und bitten um
// Nacharbeit.
//
// Die Werkzeilen folgen der Programm-rowVersion: eigenes Speichern
// übernimmt die frische Einbettung der Antwort sofort, Geschwister-
// Nachladen mit unveränderter Version lässt ungespeicherte Arbeit
// unberührt. Ersetzt ein frischer Server-Stand unerledigte Arbeit,
// erklärt die Werkbank die Ersetzung statt sie stillschweigend zu
// schlucken.

import Link from "next/link";
import { useEffect, useState } from "react";
import { LiedWahl } from "@/components/lied-wahl";
import {
  type AuftrittDetailsMitProgramm,
  liedUrl,
  type ProgrammEmbed,
  putProgrammItems,
} from "@/lib/events";
import { problemTitel } from "@/lib/uebertragung";
import { ZeitmarkenVorhanden } from "@/lib/zeitmarken";

const notizMaximal = 500;

const veralteteAenderung =
  "Das Programm wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

const ersatzFehler = "Das hat nicht geklappt. Bitte erneut versuchen.";

// Ein Werkzeile der Werkbank: serverseitige Programmpunkt-Id (stabil
// über Speicherungen, daran hängen die Zeitmarken) oder lokale
// Kennung für neue Einträge.
type WerkZeile = {
  schluessel: string;
  serverId: string | null;
  liedId: string;
  liedTitel: string | null;
  notiz: string;
};

// Zuletzt synchronisierter Server-Stand als Zeilenprojektion; Grundlage
// der Prüfung, ob ungespeicherte Arbeit vorliegt.
type SynchronZeile = {
  serverId: string | null;
  liedId: string;
  notiz: string;
};

/** Zeilen der Werkbank aus dem Server-Stand. */
function zeilenAusProgramm(programm: ProgrammEmbed | null): WerkZeile[] {
  return (programm?.items ?? []).map((punkt) => ({
    schluessel: punkt.id,
    serverId: punkt.id,
    liedId: punkt.liedId,
    liedTitel: punkt.liedTitle,
    notiz: punkt.note ?? "",
  }));
}

/** Zeilenprojektion eines Standes für den Vergleich mit der Arbeit. */
function synchronstandAusZeilen(zeilen: WerkZeile[]): SynchronZeile[] {
  return zeilen.map((zeile) => ({
    serverId: zeile.serverId,
    liedId: zeile.liedId,
    notiz: zeile.notiz,
  }));
}

/**
 * Ungespeicherte Arbeit: die Zeilen weichen vom zuletzt synchronisierten
 * Server-Stand ab (Id-Vorhandensein, Reihenfolge, Lied, Notiz).
 */
function ungespeichert(
  aktuelleZeilen: WerkZeile[],
  bekannterStand: SynchronZeile[],
): boolean {
  if (aktuelleZeilen.length !== bekannterStand.length) return true;
  return aktuelleZeilen.some((zeile, index) => {
    const bekannt = bekannterStand[index];
    return (
      zeile.serverId !== bekannt.serverId ||
      zeile.liedId !== bekannt.liedId ||
      zeile.notiz.trim() !== bekannt.notiz.trim()
    );
  });
}

// ─── Lesesaal: das Programm ──────────────────────────────────────────

function ProgrammAnzeige({ programm }: { programm: ProgrammEmbed }) {
  return (
    <ol className="programm-liste">
      {programm.items.map((punkt) => {
        const zeile = [
          punkt.voiceConfiguration
            ? `Besetzung: ${punkt.voiceConfiguration}`
            : null,
          punkt.musicalKey ? `Tonart: ${punkt.musicalKey}` : null,
        ].filter((teil): teil is string => teil !== null);
        return (
          <li className="programm-eintrag" key={punkt.id}>
            <h4 className="programm-titel">
              <span className="programm-nummer" aria-hidden="true">
                {punkt.position}.
              </span>{" "}
              <Link href={liedUrl(punkt.liedId)}>
                {punkt.liedTitle ?? "Ohne Titel"}
              </Link>
            </h4>
            {zeile.length > 0 && (
              <p className="noten-info">{zeile.join(" · ")}</p>
            )}
            {punkt.note && <p className="programm-notiz">{punkt.note}</p>}
          </li>
        );
      })}
    </ol>
  );
}

// ─── Werkbank-Zeile ──────────────────────────────────────────────────

function ProgrammZeile({
  zeile,
  stelle,
  erste,
  letzte,
  onNotiz,
  onHoch,
  onRunter,
  onEntfernen,
}: {
  zeile: WerkZeile;
  stelle: number;
  erste: boolean;
  letzte: boolean;
  onNotiz: (wert: string) => void;
  onHoch: () => void;
  onRunter: () => void;
  onEntfernen: () => void;
}) {
  const idPraefix = `programm-zeile-${zeile.schluessel}`;
  const titel = zeile.liedTitel ?? "Ohne Titel";
  return (
    <li className="programm-zeile">
      <div className="programm-zeile-kopf">
        <span className="programm-zeile-nummer" aria-hidden="true">
          {stelle}.
        </span>
        <span className="programm-zeile-titel">{titel}</span>
      </div>
      <div className="programm-zeile-werkzeuge">
        <button
          type="button"
          className="knopf-leise programm-umordnen"
          aria-label={`Eintrag ${stelle} nach oben schieben`}
          disabled={erste}
          onClick={onHoch}
        >
          <span aria-hidden="true">↑</span>
        </button>
        <button
          type="button"
          className="knopf-leise programm-umordnen"
          aria-label={`Eintrag ${stelle} nach unten schieben`}
          disabled={letzte}
          onClick={onRunter}
        >
          <span aria-hidden="true">↓</span>
        </button>
        <button
          type="button"
          className="knopf-leise"
          aria-label={`Eintrag ${stelle} entfernen`}
          onClick={onEntfernen}
        >
          Entfernen
        </button>
      </div>
      <div className="programm-zeile-notiz">
        <label htmlFor={`${idPraefix}-notiz`}>Notiz (optional)</label>
        <input
          id={`${idPraefix}-notiz`}
          type="text"
          maxLength={notizMaximal}
          value={zeile.notiz}
          onChange={(event) => onNotiz(event.target.value)}
        />
      </div>
    </li>
  );
}

// ─── Der Bereich ─────────────────────────────────────────────────────

export function AuftrittProgramm({
  auftritt,
  isEditor,
  aktualisieren,
}: {
  auftritt: AuftrittDetailsMitProgramm;
  isEditor: boolean;
  aktualisieren: () => void;
}) {
  const programm = auftritt.programme ?? null;

  const [ausgeklappt, setAusgeklappt] = useState(false);
  const [zeilen, setZeilen] = useState<WerkZeile[]>([]);
  const [synchronstand, setSynchronstand] = useState<SynchronZeile[]>([]);
  // rowVersion des Programmstands, aus dem die Zeilen zuletzt abgeleitet
  // wurden; Anker für Concurrency und Resync (null = ohne Programm).
  const [letzteGeladeneVersion, setLetzteGeladeneVersion] = useState<
    number | null
  >(null);
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [speichernBusy, setSpeichernBusy] = useState(false);

  const ungespeicherteArbeit = ungespeichert(zeilen, synchronstand);

  // Die Werkzeilen folgen dem Server-Stand: beim Aufklappen und bei jedem
  // echten Versionswechsel (Speichern, 409-Nachladen, Erscheinen des
  // Programms). Ein Geschwister-Nachladen mit unveränderter rowVersion
  // lässt die Zeilen unberührt, damit ungespeicherte Arbeit nicht
  // verloren geht; der ältere Stand des Elternteils überschreibt keine
  // schon übernommene frischere Version (eigenes Speichern ist der
  // Auftrittsabfrage voraus). Ersetzt ein frischerer Stand unerledigte
  // Arbeit, meldet sich die Werkbank: die Änderung kam von außen.
  useEffect(() => {
    if (!ausgeklappt) return;
    const version = programm?.rowVersion ?? null;
    if (version === letzteGeladeneVersion) return;
    if (
      letzteGeladeneVersion !== null &&
      (version === null || version < letzteGeladeneVersion)
    ) {
      return;
    }
    if (ungespeichert(zeilen, synchronstand)) {
      setHinweis(veralteteAenderung);
    }
    const folge = zeilenAusProgramm(programm);
    setZeilen(folge);
    setSynchronstand(synchronstandAusZeilen(folge));
    setLetzteGeladeneVersion(version);
  }, [ausgeklappt, programm, letzteGeladeneVersion, zeilen, synchronstand]);

  function neuerEintrag(liedId: string, titel: string) {
    setZeilen((bisher) => [
      ...bisher,
      {
        schluessel: crypto.randomUUID(),
        serverId: null,
        liedId,
        liedTitel: titel,
        notiz: "",
      },
    ]);
  }

  function umordnen(index: number, richtung: -1 | 1) {
    setZeilen((bisher) => {
      const ziel = index + richtung;
      if (ziel < 0 || ziel >= bisher.length) return bisher;
      const stand = [...bisher];
      const [eintrag] = stand.splice(index, 1);
      stand.splice(ziel, 0, eintrag);
      return stand;
    });
  }

  function entfernen(schluessel: string) {
    setZeilen((bisher) =>
      bisher.filter((zeile) => zeile.schluessel !== schluessel),
    );
  }

  async function speichern() {
    setSpeichernBusy(true);
    setHinweis("");
    setErfolg("");
    try {
      // Ohne Programm legt der erste PUT es an (rowVersion entfällt); mit
      // Programm zählt die zuletzt geladene Version als Concurrency-Anker
      // — auch dann, wenn die Auftrittsabfrage nach einem eigenen
      // Speichern noch unterwegs ist.
      const einbettung = await putProgrammItems(auftritt.id, {
        items: zeilen.map((zeile) => ({
          ...(zeile.serverId ? { id: zeile.serverId } : {}),
          liedId: zeile.liedId,
          note: zeile.notiz.trim(),
        })),
        ...(letzteGeladeneVersion !== null
          ? { rowVersion: letzteGeladeneVersion }
          : {}),
      });
      // Die frische Einbettung (neue Ids, neue rowVersion) kommt aus der
      // Antwort; die Zeilen folgen ihr sofort, damit ein zügiges zweites
      // Speichern den neuen Anker trägt.
      const folge = zeilenAusProgramm(einbettung);
      setLetzteGeladeneVersion(einbettung.rowVersion);
      setZeilen(folge);
      setSynchronstand(synchronstandAusZeilen(folge));
      setErfolg("Programm gespeichert. Mitglieder sehen es ab sofort.");
      aktualisieren();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        // Veralteter Stand: der frische kommt über aktualisieren(); die
        // Redaktion prüft ihn und wiederholt die Änderung. Hängen
        // Zeitmarken am Eintrag, nennt die Antwort sie beim Namen.
        const grund = await problemTitel(ursache, veralteteAenderung);
        setHinweis(
          grund.startsWith(ZeitmarkenVorhanden) ? grund : veralteteAenderung,
        );
        aktualisieren();
        return;
      }
      setHinweis(await problemTitel(ursache, ersatzFehler));
    } finally {
      setSpeichernBusy(false);
    }
  }

  return (
    <section
      className="auftritt-abschnitt auftritt-programm"
      aria-labelledby="auftritt-programm-titel"
    >
      <h3 id="auftritt-programm-titel">Programm</h3>
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
      {programm && programm.items.length > 0 ? (
        <ProgrammAnzeige programm={programm} />
      ) : (
        <p className="auftritt-leer">Das Programm wurde noch nicht erfasst.</p>
      )}
      {isEditor && (
        <details
          className="programm-verwaltung"
          aria-labelledby="programm-verwaltung-titel"
          onToggle={(event) =>
            setAusgeklappt((event.target as HTMLDetailsElement).open)
          }
        >
          <summary>
            <h4 id="programm-verwaltung-titel">Programm bearbeiten</h4>
            <span className="programm-verwaltung-umschalter" aria-hidden="true">
              <span className="programm-verwaltung-auf">Ausklappen</span>
              <span className="programm-verwaltung-zu">Einklappen</span>
            </span>
          </summary>
          <fieldset
            className="programm-verwaltung"
            aria-label="Programm bearbeiten"
          >
            {ausgeklappt && (
              <>
                <p className="programm-stand">
                  Speichern macht das Programm sofort für alle Mitglieder
                  sichtbar. Es gilt zugleich als Aufzeichnung dessen, was
                  gesungen wurde; kam es anders, ändere es hier nachträglich.
                </p>
                {zeilen.length === 0 ? (
                  <p className="auftritt-leer">
                    Das Programm ist leer. Such ein Lied aus dem Katalog und
                    füge es hinzu.
                  </p>
                ) : (
                  <ul className="programm-zeilen">
                    {zeilen.map((zeile, index) => (
                      <ProgrammZeile
                        key={zeile.schluessel}
                        zeile={zeile}
                        stelle={index + 1}
                        erste={index === 0}
                        letzte={index === zeilen.length - 1}
                        onNotiz={(wert) =>
                          setZeilen((bisher) =>
                            bisher.map((eintrag) =>
                              eintrag.schluessel === zeile.schluessel
                                ? { ...eintrag, notiz: wert }
                                : eintrag,
                            ),
                          )
                        }
                        onHoch={() => umordnen(index, -1)}
                        onRunter={() => umordnen(index, 1)}
                        onEntfernen={() => entfernen(zeile.schluessel)}
                      />
                    ))}
                  </ul>
                )}
                <LiedWahl onGewaehlt={neuerEintrag} />
                {ungespeicherteArbeit && (
                  <p className="programm-unerledigt">
                    Nicht gespeicherte Änderungen.
                  </p>
                )}
                <div className="noten-aktionen">
                  <button
                    type="button"
                    disabled={speichernBusy}
                    onClick={() => void speichern()}
                  >
                    {speichernBusy
                      ? "Wird gespeichert …"
                      : "Programm speichern"}
                  </button>
                </div>
              </>
            )}
          </fieldset>
        </details>
      )}
    </section>
  );
}
