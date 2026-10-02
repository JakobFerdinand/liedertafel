"use client";

// ARC-034: Auswertungsstand der PDF-Textauswertung als stille Zeile im
// Material-Eintrag. Die Redaktion sieht, ob der Text schon gelesen ist,
// kann ihn als Vorschau aufklappen und gescheiterte Läufe erneut starten;
// die Zeile fragt nur nach, solange die Auswertung wirklich unterwegs ist.
// Mitglieder sehen nichts (die Auswertung ist Redaktionsarbeit).

import { useEffect, useState } from "react";
import { problemTitel } from "@/lib/assets";
import {
  type ExtraktionsInfo,
  type ExtraktionsStatus,
  extraktionsStatusText,
  holeExtraktionStatus,
  starteExtraktionErneut,
} from "@/lib/extraktion";

/** Abstand zwischen zwei Nachfragen, solange die Auswertung unterwegs ist. */
const abfrageAbstandMs = 4000;

/** Umfang der offen gelegten Textvorschau; längere Texte enden mit dem Punkt. */
const vorschauLaenge = 600;

const ladeFehlerText = "Auswertungsstatus konnte nicht geladen werden.";
const wiederholErsatz =
  "Der erneute Versuch konnte nicht gestartet werden. Bitte erneut versuchen.";

/** Auswertung wartet oder läuft: Nachfragen lohnen sich weiter. */
function istLaufend(status: ExtraktionsStatus): boolean {
  return status === "queued" || status === "running";
}

/** Was die Zeile zuletzt über die eigene Revision gelernt hat. */
type Stand =
  | { art: "unbekannt" }
  | { art: "status"; info: ExtraktionsInfo }
  | { art: "ladeFehler" }
  | { art: "verweigert" }
  | { art: "wiederholFehler"; info: ExtraktionsInfo; titel: string };

/** Anfang des Textvorschau-Auszugs; längere Texte enden mit dem Punkt. */
function vorschauText(text: string): string {
  const auszug = text.slice(0, vorschauLaenge);
  return text.length > vorschauLaenge ? `${auszug}…` : auszug;
}

export function ExtraktionStatus({
  revisionId,
  isEditor,
}: {
  revisionId: string;
  isEditor: boolean;
}) {
  // Der Stand gehört zu einer Kennung: nach einem Wechsel liest sich die
  // Zeile erst wieder, wenn die frische Antwort für genau diese Revision
  // da ist (kein alter Stand, keine Erfindung).
  const [geladen, setGeladen] = useState<{
    revisionId: string;
    stand: Stand;
  } | null>(null);
  const [schickt, setSchickt] = useState(false);
  const [offen, setOffen] = useState(false);
  // Zähler der Neuanläufe: der Knopf der Fehlerzeile stößt dieselbe
  // Abfrage erneut an, die auch die Nachfragen im Lauf benutzen.
  const [neuVersuch, setNeuVersuch] = useState(0);

  // Eine Abfrage je Standwechsel: die Zeile erscheint erst, wenn der
  // Status für genau diese Revision vorliegt.
  // biome-ignore lint/correctness/useExhaustiveDependencies: neuVersuch ist Absicht — er stößt die Abfrage nur neu an, ohne selbst gelesen zu werden.
  useEffect(() => {
    if (!isEditor) return;
    const abbruch = new AbortController();
    async function holen() {
      try {
        const liste = await holeExtraktionStatus([revisionId], abbruch.signal);
        if (abbruch.signal.aborted) return;
        const info =
          liste.find((eintrag) => eintrag.revisionId === revisionId) ?? null;
        setGeladen({
          revisionId,
          stand: info ? { art: "status", info } : { art: "unbekannt" },
        });
      } catch (ursache) {
        if (abbruch.signal.aborted) return;
        // Abgelehnte Rechte sind keine Störung: die Zeile bleibt ganz weg.
        if (
          ursache instanceof Response &&
          (ursache.status === 401 || ursache.status === 403)
        ) {
          setGeladen({ revisionId, stand: { art: "verweigert" } });
          return;
        }
        setGeladen({ revisionId, stand: { art: "ladeFehler" } });
      }
    }
    void holen();
    return () => abbruch.abort();
  }, [revisionId, isEditor, neuVersuch]);

  // Nachfragen nur, solange die Auswertung wartet oder läuft; ein fertiger
  // Stand bleibt stehen, und der Abbruch räumt die Wartezeit ab.
  useEffect(() => {
    if (
      !geladen ||
      geladen.revisionId !== revisionId ||
      geladen.stand.art !== "status" ||
      !istLaufend(geladen.stand.info.status)
    ) {
      return;
    }
    const timer = window.setTimeout(
      () => setNeuVersuch((zahl) => zahl + 1),
      abfrageAbstandMs,
    );
    return () => window.clearTimeout(timer);
  }, [geladen, revisionId]);

  async function erneutStarten() {
    setSchickt(true);
    try {
      const info = await starteExtraktionErneut(revisionId);
      setGeladen({ revisionId, stand: { art: "status", info } });
    } catch (ursache) {
      // Läuft die Auswertung schon (etwa aus einem zweiten Tab), liest sich
      // die Zeile ehrlich als laufend — der Server nennt genau diesen Stand.
      if (ursache instanceof Response && ursache.status === 409) {
        setGeladen((vorher) => {
          if (!vorher || vorher.revisionId !== revisionId) return vorher;
          const info =
            vorher.stand.art === "status" ||
            vorher.stand.art === "wiederholFehler"
              ? vorher.stand.info
              : null;
          if (!info) return vorher;
          return {
            revisionId,
            stand: {
              art: "status",
              info: { ...info, status: "running", failureReason: null },
            },
          };
        });
        return;
      }
      const titel = await problemTitel(ursache, wiederholErsatz);
      setGeladen((vorher) => {
        if (!vorher || vorher.revisionId !== revisionId) return vorher;
        if (
          vorher.stand.art !== "status" &&
          vorher.stand.art !== "wiederholFehler"
        ) {
          return vorher;
        }
        return {
          revisionId,
          stand: {
            art: "wiederholFehler",
            info: vorher.stand.info,
            titel,
          },
        };
      });
    } finally {
      setSchickt(false);
    }
  }

  if (!isEditor) return null;
  const stand =
    geladen && geladen.revisionId === revisionId ? geladen.stand : null;
  if (!stand || stand.art === "unbekannt" || stand.art === "verweigert") {
    return null;
  }

  if (stand.art === "ladeFehler") {
    return (
      <div className="extraktion-status">
        <p role="alert" className="feld-fehler">
          {ladeFehlerText}{" "}
          <button
            type="button"
            onClick={() => setNeuVersuch((zahl) => zahl + 1)}
          >
            Erneut versuchen
          </button>
        </p>
      </div>
    );
  }

  const info = stand.info;
  const text = typeof info.text === "string" ? info.text : "";
  const grund =
    info.status === "failed" ? (info.failureReason?.trim() ?? "") : "";
  const vorschauId = `extraktion-vorschau-${revisionId}`;

  return (
    <div className="extraktion-status">
      <output className="extraktion-stand">
        {extraktionsStatusText(info.status)}
        {grund ? <span className="extraktion-grund"> — {grund}</span> : null}
      </output>
      {info.status === "failed" && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void erneutStarten()}
          disabled={schickt}
          aria-label="Textauswertung für diese Datei erneut starten"
        >
          {schickt ? "Wird erneut angestoßen …" : "Erneut versuchen"}
        </button>
      )}
      {info.status === "completed" && text.length > 0 && (
        <>
          <button
            type="button"
            className="knopf-leise"
            aria-expanded={offen}
            aria-controls={vorschauId}
            onClick={() => setOffen(!offen)}
          >
            {offen ? "Vorschau schließen" : "Textvorschau"}
          </button>
          {offen && (
            <p className="extraktion-vorschau" id={vorschauId}>
              {vorschauText(text)}
            </p>
          )}
        </>
      )}
      {stand.art === "wiederholFehler" && (
        <p role="alert" className="feld-fehler">
          {stand.titel}
        </p>
      )}
    </div>
  );
}
