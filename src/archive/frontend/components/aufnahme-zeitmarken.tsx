"use client";

// ARC-032: markierte Stellen („Zeitmarken“) einer ganzen Aufnahme.
// Mitglieder sehen die Lieder der Aufnahme und springen mit einem Knopf an
// den Anfang; eine Marke, deren Zeiten an einer früheren Datei genommen
// wurden, bietet keinen Sprung an (nichts springt an eine falsche Stelle).
// Die Redaktion geht die Programmpunkte des Auftritts in Reihenfolge
// durch und setzt je Lied Anfang und Ende – mit der Position des Spielers
// oder von Hand. Eine Aufnahme ohne Marken bleibt als Ganzes abspielbar.

import { useEffect, useRef, useState } from "react";
import type { Aufnahme } from "@/lib/aufnahmen";
import { problemTitel } from "@/lib/uebertragung";
import {
  bereichText,
  bestaetigeZeitmarken,
  createZeitmarke,
  deleteZeitmarke,
  type Programmeintrag,
  patchZeitmarke,
  type Zeitmarke,
  ZeitmarkeDateiErsetzt,
  type ZeitmarkenStand,
  ZeitmarkeVeraltet,
  zeitFormat,
  zeitLesen,
} from "@/lib/zeitmarken";

const SpeicherErsatz = "Das hat nicht geklappt. Bitte erneut versuchen.";

function titelVon(vorkommen: Programmeintrag): string {
  return vorkommen.liedTitle ?? "Ohne Titel";
}

function anzahlText(zahl: number, einzahl: string, mehrzahl: string): string {
  return `${zahl} ${zahl === 1 ? einzahl : mehrzahl}`;
}

/** Marken der Mitglieder: die Lieder der Aufnahme mit Sprungknopf. */
export function ZeitmarkenListe({
  aufnahme,
  stand,
  onSpringen,
}: {
  aufnahme: Aufnahme;
  stand: ZeitmarkenStand;
  onSpringen: (zeitmarke: Zeitmarke) => void;
}) {
  if (stand.passages.length === 0) return null;
  const spielbar = aufnahme.playback.state === "ready";
  const titelId = `zeitmarken-titel-${aufnahme.id}`;
  return (
    <section className="aufnahme-zeitmarken" aria-labelledby={titelId}>
      <h5 id={titelId}>Lieder in dieser Aufnahme</h5>
      <ul className="zeitmarken-liste">
        {stand.passages.map((marke) => {
          const pruefen = marke.timestampState !== "current";
          const hatZeiten =
            marke.startSeconds !== null && marke.endSeconds !== null;
          return (
            <li key={marke.id} className="zeitmarke-eintrag">
              <span className="zeitmarke-lied">{marke.liedTitle}</span>
              {hatZeiten && !pruefen ? (
                <span className="zeitmarke-zeit">
                  {bereichText(marke.startSeconds ?? 0, marke.endSeconds ?? 0)}
                </span>
              ) : (
                <span className="zeitmarke-zeit noten-info">
                  Zeitmarke wird überprüft
                </span>
              )}
              {hatZeiten && !pruefen && spielbar && (
                <button
                  type="button"
                  className="knopf-leise"
                  aria-label={`Zu „${marke.liedTitle}“ springen`}
                  onClick={() => onSpringen(marke)}
                >
                  Springen
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

type Entwurf = { start: string; ende: string };

type Pruefung =
  | { ok: true; start: number; ende: number }
  | { ok: false; meldung: string; feld: "start" | "ende" };

function pruefen(entwurf: Entwurf, dauer: number | null): Pruefung {
  if (entwurf.start.trim() === "") {
    return { ok: false, meldung: "Der Anfang fehlt.", feld: "start" };
  }
  const start = zeitLesen(entwurf.start);
  if (start === null) {
    return {
      ok: false,
      meldung:
        "Den Anfang bitte als Minuten:Sekunden angeben, zum Beispiel 12:30.",
      feld: "start",
    };
  }
  if (entwurf.ende.trim() === "") {
    return { ok: false, meldung: "Das Ende fehlt.", feld: "ende" };
  }
  const ende = zeitLesen(entwurf.ende);
  if (ende === null) {
    return {
      ok: false,
      meldung:
        "Das Ende bitte als Minuten:Sekunden angeben, zum Beispiel 12:30.",
      feld: "ende",
    };
  }
  if (ende <= start) {
    return {
      ok: false,
      meldung: "Das Ende muss nach dem Anfang liegen.",
      feld: "ende",
    };
  }
  if (dauer !== null && ende > dauer + 1) {
    return {
      ok: false,
      meldung: "Das Ende liegt hinter dem Ende der Aufnahme.",
      feld: "ende",
    };
  }
  return { ok: true, start, ende };
}

/** Marken der Redaktion: Programmpunkte in Reihenfolge, Anfang und Ende setzen. */
export function ZeitmarkenRedaktion({
  aufnahme,
  stand,
  spielerOffen,
  leseposition,
  onSpringen,
  onGeaendert,
  onDateiGewechselt,
  onMeldung,
}: {
  aufnahme: Aufnahme;
  stand: ZeitmarkenStand;
  /** Der Spieler dieser Aufnahme ist geöffnet: nur dann gibt es eine Position. */
  spielerOffen: boolean;
  leseposition: () => number | null;
  onSpringen: (zeitmarke: Zeitmarke) => void;
  /** Lädt den Stand der Marken neu (nach jedem Schreiben und bei veraltetem Stand). */
  onGeaendert: () => Promise<void>;
  /**
   * Die Datei, die Mitglieder abspielen, wurde ersetzt, seit diese Ansicht
   * gebaut wurde: Aufnahme, Spieler und Marken müssen neu geladen werden.
   */
  onDateiGewechselt: () => Promise<void>;
  onMeldung: (text: string) => void;
}) {
  const [entwuerfe, setEntwuerfe] = useState<Record<string, Entwurf>>({});
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState("");
  const [entfernenBereit, setEntfernenBereit] = useState<string | null>(null);
  const [fokusAuf, setFokusAuf] = useState<string | null>(null);
  // Das Feld, dessen Eingabe die Meldung beanstandet: markiert und fokussiert.
  const [ungueltig, setUngueltig] = useState<{
    programmeItemId: string;
    feld: "start" | "ende";
  } | null>(null);
  const [fokusFeld, setFokusFeld] = useState<{
    programmeItemId: string;
    feld: "start" | "ende";
  } | null>(null);
  const fehlerRef = useRef<HTMLParagraphElement | null>(null);
  const fokusFehler = useRef(false);
  const startFelder = useRef(new Map<string, HTMLInputElement>());
  const endeFelder = useRef(new Map<string, HTMLInputElement>());
  const formatId = `zeitmarken-format-${aufnahme.id}`;
  const fehlerId = `zeitmarken-fehler-${aufnahme.id}`;

  useEffect(() => {
    if (fehler && fokusFehler.current) {
      fokusFehler.current = false;
      fehlerRef.current?.focus();
    }
  }, [fehler]);

  // Nach der Meldung (sie steht schon im Alarmbereich und ist über
  // aria-describedby am Feld) geht der Fokus ins beanstandete Feld.
  useEffect(() => {
    if (!fokusFeld) return;
    const felder = fokusFeld.feld === "start" ? startFelder : endeFelder;
    felder.current.get(fokusFeld.programmeItemId)?.focus();
    setFokusFeld(null);
  }, [fokusFeld]);

  useEffect(() => {
    if (!fokusAuf) return;
    const feld = startFelder.current.get(fokusAuf);
    if (!feld) return;
    feld.focus();
    setFokusAuf(null);
  }, [fokusAuf]);

  const vorkommen = stand.entries ?? [];
  const marken = new Map(stand.passages.map((marke) => [marke.id, marke]));
  const dauer = stand.durationSeconds ?? null;
  const zuPruefen = stand.passages.filter(
    (marke) => marke.timestampState !== "current",
  );

  function wertVon(eintrag: Programmeintrag): Entwurf {
    const entwurf = entwuerfe[eintrag.programmeItemId];
    if (entwurf) return entwurf;
    const marke = eintrag.passageId ? marken.get(eintrag.passageId) : undefined;
    return {
      start: marke?.startSeconds != null ? zeitFormat(marke.startSeconds) : "",
      ende: marke?.endSeconds != null ? zeitFormat(marke.endSeconds) : "",
    };
  }

  function aendern(eintrag: Programmeintrag, teil: Partial<Entwurf>) {
    setUngueltig((vorher) =>
      vorher?.programmeItemId === eintrag.programmeItemId ? null : vorher,
    );
    setEntwuerfe((vorher) => ({
      ...vorher,
      [eintrag.programmeItemId]: { ...wertVon(eintrag), ...teil },
    }));
  }

  function uebernehmen(eintrag: Programmeintrag, feld: "start" | "ende") {
    const position = leseposition();
    if (position === null) return;
    aendern(eintrag, { [feld]: zeitFormat(position) });
  }

  function meldeFehler(text: string) {
    fokusFehler.current = true;
    setFehler(text);
  }

  async function speichern(eintrag: Programmeintrag) {
    if (busy) return;
    const titel = titelVon(eintrag);
    setFehler("");
    setUngueltig(null);
    const ergebnis = pruefen(wertVon(eintrag), dauer);
    if (!ergebnis.ok) {
      setFehler(`„${titel}“: ${ergebnis.meldung}`);
      setUngueltig({
        programmeItemId: eintrag.programmeItemId,
        feld: ergebnis.feld,
      });
      setFokusFeld({
        programmeItemId: eintrag.programmeItemId,
        feld: ergebnis.feld,
      });
      return;
    }
    // Zeiten werden immer an der Datei festgehalten, die die Redaktion beim
    // Bauen dieser Ansicht gesehen hat; ohne abspielbare Datei gibt es keine.
    const revision = stand.playbackDateiId ?? null;
    if (revision === null) {
      meldeFehler(
        `„${titel}“: Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken setzen lassen.`,
      );
      return;
    }
    const marke = eintrag.passageId ? marken.get(eintrag.passageId) : undefined;
    setBusy(eintrag.programmeItemId);
    try {
      if (marke?.editor) {
        await patchZeitmarke(aufnahme.id, marke.id, {
          startSeconds: ergebnis.start,
          endSeconds: ergebnis.ende,
          expectedVersion: marke.editor.version,
          expectedPlaybackDateiId: revision,
        });
      } else {
        await createZeitmarke(aufnahme.id, {
          programmeItemId: eintrag.programmeItemId,
          startSeconds: ergebnis.start,
          endSeconds: ergebnis.ende,
          expectedPlaybackDateiId: revision,
        });
      }
      setEntwuerfe((vorher) => {
        const { [eintrag.programmeItemId]: _weg, ...rest } = vorher;
        return rest;
      });
      onMeldung(`Zeitmarke für „${titel}“ gespeichert.`);
      await onGeaendert();
      // Weiter mit dem nächsten Lied ohne Marke, sonst bleibt der Fokus hier.
      const stelle = vorkommen.findIndex(
        (kandidat) => kandidat.programmeItemId === eintrag.programmeItemId,
      );
      const naechstes = vorkommen
        .slice(stelle + 1)
        .find((kandidat) => kandidat.passageId === null);
      setFokusAuf((naechstes ?? eintrag).programmeItemId);
    } catch (ursache) {
      const text = await problemTitel(ursache, SpeicherErsatz);
      if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        text === ZeitmarkeDateiErsetzt
      ) {
        // Die Datei hat gewechselt: Aufnahme, Spieler und Marken werden neu
        // geladen. Die getippten Zeiten bleiben stehen; sie gelten der alten
        // Datei und werden erst nach dem Prüfen erneut gespeichert.
        meldeFehler(
          `„${titel}“: ${text} Die Aufnahme, der Spieler und die Zeitmarken wurden neu geladen; deine Eingaben stehen noch da. Bitte öffne den Spieler neu und prüfe die Zeiten gegen die neue Datei, bevor du erneut speicherst.`,
        );
        await onDateiGewechselt();
      } else if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        text === ZeitmarkeVeraltet
      ) {
        // Veralteter Stand: neu laden; die Eingabe bezog sich auf etwas, das
        // es so nicht mehr gibt.
        setEntwuerfe((vorher) => {
          const { [eintrag.programmeItemId]: _weg, ...rest } = vorher;
          return rest;
        });
        meldeFehler(
          `„${titel}“: ${text} Der aktuelle Stand wurde geladen; bitte die Änderung erneut vornehmen.`,
        );
        await onGeaendert();
      } else if (ursache instanceof Response && ursache.status === 404) {
        meldeFehler(`„${titel}“: ${text}`);
        await onGeaendert();
      } else {
        // Ungültig oder in diesem Zustand nicht erlaubt: die Eingabe bleibt.
        meldeFehler(`„${titel}“: ${text}`);
      }
    } finally {
      setBusy("");
    }
  }

  async function entfernen(eintrag: Programmeintrag, marke: Zeitmarke) {
    if (busy || !marke.editor) return;
    const titel = titelVon(eintrag);
    setFehler("");
    setBusy(eintrag.programmeItemId);
    try {
      await deleteZeitmarke(aufnahme.id, marke.id, marke.editor.version);
      setEntfernenBereit(null);
      setEntwuerfe((vorher) => {
        const { [eintrag.programmeItemId]: _weg, ...rest } = vorher;
        return rest;
      });
      onMeldung(`Zeitmarke für „${titel}“ entfernt.`);
      await onGeaendert();
      setFokusAuf(eintrag.programmeItemId);
    } catch (ursache) {
      setEntfernenBereit(null);
      const text = await problemTitel(ursache, SpeicherErsatz);
      meldeFehler(
        ursache instanceof Response &&
          ursache.status === 409 &&
          text === ZeitmarkeVeraltet
          ? `„${titel}“: ${text} Der aktuelle Stand wurde geladen; bitte erneut entscheiden.`
          : `„${titel}“: ${text}`,
      );
      if (ursache instanceof Response && ursache.status !== 400) {
        await onGeaendert();
      }
    } finally {
      setBusy("");
    }
  }

  async function allePruefen() {
    if (busy) return;
    setFehler("");
    const revision = stand.playbackDateiId ?? null;
    if (revision === null) {
      meldeFehler(
        "Die Aufnahme hat noch keine abspielbare Datei, an der sich Zeitmarken bestätigen lassen.",
      );
      return;
    }
    setBusy("pruefen");
    try {
      // Genau die Marken, die diese Ansicht als zu prüfen aufgelistet hat,
      // mit dem Stand, den sie gesehen hat.
      await bestaetigeZeitmarken(
        aufnahme.id,
        revision,
        zuPruefen.flatMap((marke) =>
          marke.editor
            ? [{ id: marke.id, expectedVersion: marke.editor.version }]
            : [],
        ),
      );
      onMeldung(
        `${anzahlText(zuPruefen.length, "Zeitmarke", "Zeitmarken")} für die aktuelle Datei bestätigt.`,
      );
      await onGeaendert();
    } catch (ursache) {
      const text = await problemTitel(ursache, SpeicherErsatz);
      if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        text === ZeitmarkeDateiErsetzt
      ) {
        meldeFehler(
          `${text} Die Aufnahme, der Spieler und die Zeitmarken wurden neu geladen. Bitte prüfe die Zeiten gegen die neue Datei, bevor du sie bestätigst.`,
        );
        await onDateiGewechselt();
      } else if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        text === ZeitmarkeVeraltet
      ) {
        meldeFehler(
          `${text} Der aktuelle Stand wurde geladen; bitte erneut prüfen und bestätigen.`,
        );
        await onGeaendert();
      } else {
        meldeFehler(text);
      }
    } finally {
      setBusy("");
    }
  }

  function istUngueltig(
    eintrag: Programmeintrag,
    feld: "start" | "ende",
  ): boolean {
    return (
      ungueltig?.programmeItemId === eintrag.programmeItemId &&
      ungueltig.feld === feld
    );
  }

  // Das Formathinweis-Element beschreibt jedes Zeitfeld; ein beanstandetes
  // Feld nennt zusätzlich die Meldung.
  function beschreibung(
    eintrag: Programmeintrag,
    feld: "start" | "ende",
  ): string {
    return istUngueltig(eintrag, feld) ? `${formatId} ${fehlerId}` : formatId;
  }

  // Überschneidungen sind ein Hinweis, kein Fehler: Zeiten werden oft in
  // mehreren Schritten zurechtgerückt.
  function ueberschneidung(marke: Zeitmarke): string | null {
    if (marke.startSeconds === null || marke.endSeconds === null) return null;
    const andere = stand.passages.filter(
      (kandidat) =>
        kandidat.id !== marke.id &&
        kandidat.startSeconds !== null &&
        kandidat.endSeconds !== null &&
        kandidat.startSeconds < (marke.endSeconds ?? 0) &&
        (kandidat.endSeconds ?? 0) > (marke.startSeconds ?? 0),
    );
    if (andere.length === 0) return null;
    return `Überschneidet sich mit ${andere.map((kandidat) => `„${kandidat.liedTitle}“`).join(", ")}.`;
  }

  return (
    <fieldset className="aufnahme-zeitmarken-redaktion">
      <legend>Zeitmarken · {aufnahme.label}</legend>
      <p className="noten-info">
        Markiere, wo ein Lied des Programms in dieser Aufnahme beginnt und
        endet. Die ganze Aufnahme bleibt auch ohne Marken abspielbar; Marken
        ändern das Programm nicht.
      </p>
      {zuPruefen.length > 0 && (
        <div className="aufnahme-zustand">
          <p>
            {zuPruefen.length === 1
              ? "1 Zeitmarke wurde für eine frühere Datei gesetzt und muss geprüft werden."
              : `${zuPruefen.length} Zeitmarken wurden für eine frühere Datei gesetzt und müssen geprüft werden.`}{" "}
            Mitglieder bekommen dafür keinen Sprung, bis sie geprüft sind.
          </p>
          <p className="noten-info">
            Bestätige alle nur, wenn die Zeiten auch in der aktuellen Datei
            stimmen; sonst setze die einzelnen Zeiten neu.
          </p>
          <div className="noten-aktionen">
            <button
              type="button"
              aria-disabled={busy !== ""}
              onClick={() => void allePruefen()}
            >
              Alle Zeitmarken für die aktuelle Datei bestätigen
            </button>
          </div>
        </div>
      )}
      {vorkommen.length === 0 ? (
        <p className="noten-info">
          Zu diesem Auftritt gibt es noch kein Programm. Lege zuerst das
          Programm an, dann lassen sich seine Lieder in der Aufnahme markieren.
        </p>
      ) : (
        <>
          <p className="noten-info" id={formatId}>
            Zeit als m:ss oder h:mm:ss angeben, zum Beispiel 12:30 oder
            1:02:03,5.
          </p>
          {!spielerOffen && (
            <p className="noten-info">
              Zum Übernehmen der Position den Spieler dieser Aufnahme öffnen
              (Ansehen oder Anhören).
            </p>
          )}
          <ul className="zeitmarken-redaktion-liste">
            {vorkommen.map((eintrag) => {
              const titel = titelVon(eintrag);
              const marke = eintrag.passageId
                ? marken.get(eintrag.passageId)
                : undefined;
              const werte = wertVon(eintrag);
              const beschaeftigt = busy === eintrag.programmeItemId;
              const hinweis = marke ? ueberschneidung(marke) : null;
              const idStart = `zeitmarke-start-${aufnahme.id}-${eintrag.programmeItemId}`;
              const idEnde = `zeitmarke-ende-${aufnahme.id}-${eintrag.programmeItemId}`;
              return (
                <li
                  key={eintrag.programmeItemId}
                  className="zeitmarke-zeile"
                  data-programme-item-id={eintrag.programmeItemId}
                >
                  <div className="zeitmarke-kopf">
                    <strong>{titel}</strong>
                    {!marke && (
                      <span className="noten-info">Noch nicht markiert</span>
                    )}
                    {marke && marke.timestampState !== "current" && (
                      <span className="zeitmarke-pruefen">Zu prüfen</span>
                    )}
                  </div>
                  {marke && marke.startSeconds !== null && (
                    <p className="noten-info">
                      Markiert:{" "}
                      {bereichText(marke.startSeconds, marke.endSeconds ?? 0)}
                    </p>
                  )}
                  {hinweis && <p className="noten-info">{hinweis}</p>}
                  <div className="zeitmarke-felder">
                    <div>
                      <label htmlFor={idStart}>
                        Anfang
                        <span className="visually-hidden"> „{titel}“</span>
                      </label>
                      <input
                        id={idStart}
                        type="text"
                        inputMode="text"
                        autoComplete="off"
                        placeholder="12:30"
                        aria-describedby={beschreibung(eintrag, "start")}
                        aria-invalid={
                          istUngueltig(eintrag, "start") || undefined
                        }
                        value={werte.start}
                        ref={(element) => {
                          if (element) {
                            startFelder.current.set(
                              eintrag.programmeItemId,
                              element,
                            );
                          } else {
                            startFelder.current.delete(eintrag.programmeItemId);
                          }
                        }}
                        onChange={(ereignis) =>
                          aendern(eintrag, { start: ereignis.target.value })
                        }
                      />
                      <button
                        type="button"
                        className="knopf-leise"
                        disabled={!spielerOffen}
                        aria-label={`Aktuelle Position als Anfang für „${titel}“ übernehmen`}
                        onClick={() => uebernehmen(eintrag, "start")}
                      >
                        Position übernehmen
                      </button>
                    </div>
                    <div>
                      <label htmlFor={idEnde}>
                        Ende
                        <span className="visually-hidden"> „{titel}“</span>
                      </label>
                      <input
                        id={idEnde}
                        type="text"
                        inputMode="text"
                        autoComplete="off"
                        placeholder="15:40"
                        aria-describedby={beschreibung(eintrag, "ende")}
                        aria-invalid={
                          istUngueltig(eintrag, "ende") || undefined
                        }
                        ref={(element) => {
                          if (element) {
                            endeFelder.current.set(
                              eintrag.programmeItemId,
                              element,
                            );
                          } else {
                            endeFelder.current.delete(eintrag.programmeItemId);
                          }
                        }}
                        value={werte.ende}
                        onChange={(ereignis) =>
                          aendern(eintrag, { ende: ereignis.target.value })
                        }
                      />
                      <button
                        type="button"
                        className="knopf-leise"
                        disabled={!spielerOffen}
                        aria-label={`Aktuelle Position als Ende für „${titel}“ übernehmen`}
                        onClick={() => uebernehmen(eintrag, "ende")}
                      >
                        Position übernehmen
                      </button>
                    </div>
                  </div>
                  <div className="noten-aktionen">
                    <button
                      type="button"
                      aria-disabled={beschaeftigt}
                      aria-label={`Zeitmarke für „${titel}“ speichern`}
                      onClick={() => void speichern(eintrag)}
                    >
                      {beschaeftigt ? "Wird gespeichert …" : "Speichern"}
                    </button>
                    {marke && marke.startSeconds !== null && (
                      <button
                        type="button"
                        className="knopf-leise"
                        aria-label={`Zu „${titel}“ springen`}
                        disabled={aufnahme.playback.state !== "ready"}
                        onClick={() => onSpringen(marke)}
                      >
                        Springen
                      </button>
                    )}
                    {marke &&
                      (entfernenBereit === marke.id ? (
                        <>
                          <button
                            type="button"
                            className="knopf-leise"
                            aria-label={`Zeitmarke für „${titel}“ wirklich entfernen`}
                            onClick={() => void entfernen(eintrag, marke)}
                          >
                            Wirklich entfernen
                          </button>
                          <button
                            type="button"
                            className="knopf-leise"
                            aria-label={`Entfernen von „${titel}“ abbrechen`}
                            onClick={() => setEntfernenBereit(null)}
                          >
                            Abbrechen
                          </button>
                        </>
                      ) : (
                        <button
                          type="button"
                          className="knopf-leise"
                          aria-label={`Zeitmarke für „${titel}“ entfernen`}
                          onClick={() => setEntfernenBereit(marke.id)}
                        >
                          Entfernen
                        </button>
                      ))}
                  </div>
                </li>
              );
            })}
          </ul>
        </>
      )}
      <div role="alert">
        {fehler && (
          <p
            className="feld-fehler"
            id={fehlerId}
            tabIndex={-1}
            ref={fehlerRef}
          >
            {fehler}
          </p>
        )}
      </div>
    </fieldset>
  );
}
