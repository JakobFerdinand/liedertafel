"use client";

// ARC-026: Programm des Auftritts — Lesesaal für Mitglieder (geordnete
// Programmpunkte mit tieflinkender Liedseite) und Redaktions-Werkbank
// nach der Rezeptur des Notenbereichs: je Eintrag eine Zeile mit
// Lied, Notiz, Umordnen (↑/↓) und Entfernen; Speichern (PUT,
// ganzer geordneter Ersatz) und Veröffentlichen trennen Entwurf und
// Mitgliedssicht. Veraltete Stände laden den frischen Stand und bitten
// um Nacharbeit.
//
// Die Werkzeilen folgen der Programm-rowVersion: eigenes Speichern und
// Veröffentlichen übernehmen die frische Einbettung der Antwort sofort,
// Geschwister-Nachladen mit unveränderter Version lässt ungespeicherte
// Arbeit unberührt. Nach einer Veröffentlichung startet der Entwurf als
// Kopie der veröffentlichten Liste — der nächste PUT legt jeden
// Eintrag neu an (Vertragssprache).
//
// ARC-027: die Werkbank zeigt ehrliche Stände — Entwurf und
// Mitgliedersicht mit Revision und Zeitpunkt, darunter die eingefrorenen
// früheren Ständen. Ersetzt ein frischer Server-Stand unerledigte
// Arbeit, erklärt die Werkbank die Ersetzung statt sie stillschweigend
// zu schlucken.

import Link from "next/link";
import { useEffect, useState } from "react";
import { LiedWahl } from "@/components/lied-wahl";
import {
  type AuftrittDetailsMitProgramm,
  liedUrl,
  type ProgrammRevision,
  type ProgrammRevisionVeroeffentlicht,
  publishedAtText,
  publishProgramm,
  putProgrammItems,
} from "@/lib/events";
import { problemTitel } from "@/lib/uebertragung";

const notizMaximal = 500;

const veralteteAenderung =
  "Der Programmentwurf wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

const ersatzFehler = "Das hat nicht geklappt. Bitte erneut versuchen.";

const ungespeicherteAenderungen =
  "Es gibt nicht gespeicherte Änderungen. Speichere den Entwurf erst, um sie zu veröffentlichen.";

// Ein Werkzeile der Workbench: serverseitige Programmpunkt-Id (stabil
// über Speicherungen) oder lokale Entwurfskennung für neue Einträge.
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

/**
 * Zeilen der Werkbank aus dem Server-Stand: mit Arbeitsrevision tragen
 * sie die stabilen Programmpunkt-Ids; ohne Arbeitsrevision (nach einer
 * Veröffentlichung) startet der Entwurf als Kopie der veröffentlichten
 * Liste — die Zeilen tragen keine Server-Ids, der nächste PUT legt
 * jeden Eintrag neu an (Vertragssprache).
 */
function zeilenAusRevisionen(
  working: ProgrammRevision | null,
  published: ProgrammRevisionVeroeffentlicht | null,
): { zeilen: WerkZeile[]; kopie: boolean } {
  if (working !== null) {
    return {
      kopie: false,
      zeilen: working.items.map((punkt) => ({
        schluessel: punkt.id,
        serverId: punkt.id,
        liedId: punkt.liedId,
        liedTitel: punkt.liedTitle,
        notiz: punkt.note ?? "",
      })),
    };
  }
  // Ohne Arbeitsrevision (nach einer Veröffentlichung) startet der
  // Entwurf als Kopie der veröffentlichten Liste: die Zeilen tragen
  // keine Server-Ids, der nächste PUT legt jeden Eintrag neu an.
  return {
    kopie: published !== null,
    zeilen: (published?.items ?? []).map((punkt) => ({
      schluessel: punkt.id,
      serverId: null,
      liedId: punkt.liedId,
      liedTitel: punkt.liedTitle,
      notiz: punkt.note ?? "",
    })),
  };
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
      (zeile.serverId !== null) !== (bekannt.serverId !== null) ||
      zeile.liedId !== bekannt.liedId ||
      zeile.notiz.trim() !== bekannt.notiz.trim()
    );
  });
}

// ─── Lesesaal: die veröffentlichte Revision ──────────────────────────

function VeröffentlichtListe({
  revision,
}: {
  revision: ProgrammRevisionVeroeffentlicht;
}) {
  return (
    <ol className="programm-liste">
      {revision.items.map((punkt) => {
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
  const veroeffentlicht = programm?.published ?? null;
  const entwurf = programm?.working ?? null;

  const [ausgeklappt, setAusgeklappt] = useState(false);
  const [zeilen, setZeilen] = useState<WerkZeile[]>([]);
  const [synchronstand, setSynchronstand] = useState<SynchronZeile[]>([]);
  // true, wenn die Zeilen als Kopie der veröffentlichten Liste starten
  // (nach einer Veröffentlichung, noch ohne eigenen Entwurf).
  const [entwurfKopie, setEntwurfKopie] = useState(false);
  // rowVersion des Programmstands, aus dem die Zeilen zuletzt abgeleitet
  // wurden; Anker für Concurrency und Resync (null = ohne Programm).
  const [letzteGeladeneVersion, setLetzteGeladeneVersion] = useState<
    number | null
  >(null);
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [speichernBusy, setSpeichernBusy] = useState(false);
  const [veröffentlichenBusy, setVeröffentlichenBusy] = useState(false);

  // ARC-027: ungespeicherte Arbeit meldet sich an der Werkbank, sobald
  // die Zeilen vom synchronisierten Stand abweichen — sie legt sich nach
  // dem Speichern wieder still.
  const ungespeicherteArbeit = ungespeichert(zeilen, synchronstand);

  // Die Werkzeilen folgen dem Server-Stand: beim Aufklappen und bei jedem
  // echten Versionswechsel (Speichern, 409-Nachladen, Veröffentlichen,
  // Erscheinen des Programms). Ein Geschwister-Nachladen mit unveränderter
  // rowVersion lässt die Zeilen unberührt, damit ungespeicherte Arbeit
  // nicht verloren geht; der ältere Stand des Elternteils überschreibt
  // keine schon übernommene frischere Version (eigenes Speichern ist
  // der Auftrittsabfrage voraus). Ersetzt ein frischerer Stand unerledigte
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
    // ARC-027: liegt ungespeicherte Arbeit vor, stammt die Ablösung von
    // außen — eigenes Speichern übernimmt die frische Einbettung direkt,
    // ohne diesen Weg. Der Hinweis erklärt die Ersetzung, bevor die
    // Zeilen dem frischen Stand folgen.
    if (ungespeichert(zeilen, synchronstand)) {
      setHinweis(veralteteAenderung);
    }
    const folge = zeilenAusRevisionen(entwurf, veroeffentlicht);
    setZeilen(folge.zeilen);
    setSynchronstand(synchronstandAusZeilen(folge.zeilen));
    setEntwurfKopie(folge.kopie);
    setLetzteGeladeneVersion(version);
    // Zeilen und Synchronstand in den Abhängigkeiten: der Abgleich liest
    // den aktuellen Arbeitsstand, der Versionswechsel entscheidet weiter.
    // Passt die Version, kehrt der Effekt früh zurück — Tastenschläge
    // kosten nichts als den Vergleich.
  }, [
    ausgeklappt,
    programm,
    entwurf,
    veroeffentlicht,
    letzteGeladeneVersion,
    zeilen,
    synchronstand,
  ]);

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
    try {
      // Ohne Programm legt der erste PUT es still an (rowVersion
      // entfällt); mit Programm zählt die zuletzt geladene Version als
      // Concurrency-Anker — auch dann, wenn die Auftrittsabfrage nach
      // einem eigenen Speichern noch unterwegs ist.
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
      // Speichern den neuen Anker trägt. Das Nachladen des Elternteils
      // bringt dieselbe Version und löst keinen zweiten Resync aus.
      const folge = zeilenAusRevisionen(
        einbettung.working,
        einbettung.published,
      );
      setLetzteGeladeneVersion(einbettung.rowVersion);
      setZeilen(folge.zeilen);
      setSynchronstand(synchronstandAusZeilen(folge.zeilen));
      setEntwurfKopie(folge.kopie);
      setErfolg("Programmentwurf gespeichert.");
      setHinweis("");
      aktualisieren();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        // Veralteter Stand: der frische kommt über aktualisieren(); die
        // Redaktion prüft ihn und wiederholt die Änderung (Vertragssprache).
        setHinweis(veralteteAenderung);
        aktualisieren();
        return;
      }
      setHinweis(await problemTitel(ursache, ersatzFehler));
    } finally {
      setSpeichernBusy(false);
    }
  }

  async function veroeffentlichen() {
    if (letzteGeladeneVersion === null) {
      setHinweis("Es liegt noch kein Programmentwurf vor.");
      return;
    }
    // Nichts wird stillschweigend verworfen: ungespeicherte Arbeit
    // bremst die Veröffentlichung, bis der Entwurf gespeichert ist.
    if (ungespeichert(zeilen, synchronstand)) {
      setHinweis(ungespeicherteAenderungen);
      return;
    }
    setVeröffentlichenBusy(true);
    setHinweis("");
    try {
      const einbettung = await publishProgramm(
        auftritt.id,
        letzteGeladeneVersion,
      );
      // Nach der Veröffentlichung ist der Entwurf weg: die Zeilen starten
      // als Kopie der veröffentlichten Liste (ohne Server-Ids), der
      // nächste PUT legt jeden Eintrag neu an.
      const folge = zeilenAusRevisionen(
        einbettung.working,
        einbettung.published,
      );
      setLetzteGeladeneVersion(einbettung.rowVersion);
      setZeilen(folge.zeilen);
      setSynchronstand(synchronstandAusZeilen(folge.zeilen));
      setEntwurfKopie(folge.kopie);
      setErfolg("Programm veröffentlicht. Mitglieder sehen es ab sofort.");
      setHinweis("");
      aktualisieren();
    } catch (ursache) {
      // Auch der Veröffentlichungssatz kennt den veralteten Stand (409)
      // und die nicht verfügbaren Fassungen; die Vertragssprache erklärt
      // beides schon.
      setHinweis(await problemTitel(ursache, ersatzFehler));
      if (ursache instanceof Response && ursache.status === 409) {
        aktualisieren();
      }
    } finally {
      setVeröffentlichenBusy(false);
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
      {/* Lesesaal: die veröffentlichte Revision liest sich für alle;
          der Entwurf bleibt den Werkbank-Werkzeugen vorbehalten. */}
      {veroeffentlicht ? (
        <>
          <VeröffentlichtListe revision={veroeffentlicht} />
          <p className="noten-info">
            Veröffentlicht am {publishedAtText(veroeffentlicht.publishedAt)}
          </p>
        </>
      ) : isEditor && programm !== null ? (
        // Ehrlich zur Redaktion: ein Entwurf existiert, es ist nur noch
        // nichts veröffentlicht; Mitglieder lesen dieselbe Situation als
        // „noch nicht erfasst", weil sie den Entwurf nicht sehen.
        <p className="auftritt-leer">Noch nichts veröffentlicht.</p>
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
            <h4 id="programm-verwaltung-titel">Programm verwalten</h4>
            <span className="programm-verwaltung-umschalter" aria-hidden="true">
              <span className="programm-verwaltung-auf">Ausklappen</span>
              <span className="programm-verwaltung-zu">Einklappen</span>
            </span>
          </summary>
          <fieldset
            className="programm-verwaltung"
            aria-label="Programm verwalten"
          >
            {ausgeklappt && (
              <>
                {/* ARC-027 Standzeilen: ehrlich in jedem Zustand — was der
                    Entwurf ist und was die Mitglieder lesen. */}
                <p className="programm-stand">
                  {entwurf
                    ? `Entwurf: Revision ${entwurf.number} · Letzte Änderung: ${publishedAtText(entwurf.updatedAt)}`
                    : "Entwurf: noch keiner gespeichert."}
                </p>
                <p className="programm-stand">
                  {veroeffentlicht
                    ? `Mitgliedersicht: Revision ${veroeffentlicht.number} · veröffentlicht am ${publishedAtText(veroeffentlicht.publishedAt)}`
                    : "Mitgliedersicht: noch nichts veröffentlicht."}
                </p>
                {/* Frühere Fassungen: die eingefrorenen Veröffentlichungen
                    vor der neuesten (Verlaufsrevisionen der Werkbank). */}
                {programm?.history && programm.history.length > 0 && (
                  <div className="programm-fruehere">
                    <p className="programm-fruehere-einleitung">
                      Frühere Fassungen
                    </p>
                    {programm.history.map((fassung) => (
                      <p className="programm-fruehere-zeile" key={fassung.id}>
                        Revision {fassung.number} · veröffentlicht am{" "}
                        {publishedAtText(fassung.publishedAt)}
                      </p>
                    ))}
                  </div>
                )}
                {entwurfKopie && (
                  <p className="noten-info">
                    Der neue Entwurf beginnt als Kopie der veröffentlichten
                    Liste; die veröffentlichte Fassung bleibt unverändert
                    stehen.
                  </p>
                )}
                {zeilen.length === 0 ? (
                  <p className="auftritt-leer">
                    Der Entwurf ist leer. Such ein Lied aus dem Katalog und füge
                    es hinzu.
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
                    {speichernBusy ? "Wird gespeichert …" : "Entwurf speichern"}
                  </button>
                  <button
                    type="button"
                    disabled={veröffentlichenBusy}
                    onClick={() => void veroeffentlichen()}
                  >
                    {veröffentlichenBusy
                      ? "Wird veröffentlicht …"
                      : "Veröffentlichen"}
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
