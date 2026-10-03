"use client";

import { useEffect, useRef, useState } from "react";
import { useExtraktionStand } from "@/components/extraktion-uebersicht";
import { problemTitel } from "@/lib/assets";
import {
  extraktionsStatusText,
  starteExtraktionErneut,
} from "@/lib/extraktion";
import { notenAngabenTeile } from "@/lib/songs";

const vorschauLaenge = 4000;
const ladeFehlerText = "Auswertungsstatus konnte nicht geladen werden.";
const wiederholErsatz =
  "Der erneute Versuch konnte nicht gestartet werden. Bitte erneut versuchen.";

export function ExtraktionStatus({
  revisionId,
  isEditor,
  angabenBekannt = true,
  onErkannt,
}: {
  revisionId: string;
  isEditor: boolean;
  /** Trägt der Eintrag die erkannten Angaben schon? */
  angabenBekannt?: boolean;
  /** Frisch erkannte Angaben liegen vor: der Eintrag soll neu laden. */
  onErkannt?: () => void;
}) {
  const { info, geladen, fehler, aktualisiereExtraktion, setzeExtraktion } =
    useExtraktionStand(revisionId, isEditor);
  const abbruchRef = useRef<AbortController | null>(null);
  const [interaktion, setInteraktion] = useState({
    revisionId,
    schickt: false,
    offen: false,
    titel: "",
    verweigert: false,
  });
  const aktuell = interaktion.revisionId === revisionId;
  const schickt = aktuell && interaktion.schickt;
  const offen = aktuell && interaktion.offen;
  const titel = aktuell ? interaktion.titel : "";

  useEffect(() => {
    setInteraktion({
      revisionId,
      schickt: false,
      offen: false,
      titel: "",
      verweigert: false,
    });
    return () => {
      abbruchRef.current?.abort();
      abbruchRef.current = null;
    };
  }, [revisionId]);

  // Eine eben fertige Auswertung benennt den Eintrag ohne Neuladen der
  // Seite; je Revision höchstens eine Nachfrage.
  const gemeldetRef = useRef<string | null>(null);
  const neuErkannt =
    !angabenBekannt && info?.status === "completed" && Boolean(info.facts);
  useEffect(() => {
    if (!neuErkannt || gemeldetRef.current === revisionId) return;
    gemeldetRef.current = revisionId;
    onErkannt?.();
  }, [neuErkannt, revisionId, onErkannt]);

  async function erneutStarten() {
    if (abbruchRef.current) return;
    const abbruch = new AbortController();
    abbruchRef.current = abbruch;
    setInteraktion((vorher) => ({ ...vorher, schickt: true, titel: "" }));
    try {
      const neu = await starteExtraktionErneut(revisionId, abbruch.signal);
      if (abbruch.signal.aborted) return;
      setzeExtraktion(neu);
      aktualisiereExtraktion();
    } catch (ursache) {
      if (abbruch.signal.aborted) return;
      if (
        ursache instanceof Response &&
        (ursache.status === 401 || ursache.status === 403)
      ) {
        setInteraktion((vorher) => ({ ...vorher, verweigert: true }));
        return;
      }
      const serverTitel = await problemTitel(ursache, wiederholErsatz);
      if (abbruch.signal.aborted) return;
      // Nur dieser konkrete Serverstand bestätigt einen laufenden Versuch.
      if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        serverTitel === "Die Auswertung läuft bereits." &&
        info
      ) {
        setzeExtraktion({ ...info, status: "running", failureReason: null });
        return;
      }
      setInteraktion((vorher) => ({ ...vorher, titel: serverTitel }));
      aktualisiereExtraktion();
    } finally {
      if (!abbruch.signal.aborted) {
        abbruchRef.current = null;
        setInteraktion((vorher) => ({ ...vorher, schickt: false }));
      }
    }
  }

  if (
    !isEditor ||
    !geladen ||
    fehler === "verweigert" ||
    (aktuell && interaktion.verweigert)
  ) {
    return null;
  }
  // Der bereinigte Text liest sich wie der gesungene; ältere Antworten
  // ohne ihn zeigen den Rohtext.
  const text =
    (typeof info?.cleanText === "string" && info.cleanText) ||
    (typeof info?.text === "string" ? info.text : "");
  const angaben = [
    info?.facts?.voice ? `Stimme ${info.facts.voice}` : null,
    ...notenAngabenTeile(info?.facts),
  ].filter((teil): teil is string => Boolean(teil));
  const grund =
    info?.status === "failed" ? (info.failureReason?.trim() ?? "") : "";
  const vorschauId = `extraktion-vorschau-${revisionId}`;

  return (
    <div className="extraktion-status">
      {info && (
        <output className="extraktion-stand">
          {extraktionsStatusText(info.status)}
          {grund ? <span className="extraktion-grund"> — {grund}</span> : null}
        </output>
      )}
      {!fehler && (!info || info.status === "failed") && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void erneutStarten()}
          disabled={schickt}
          aria-label={
            info
              ? "Textauswertung für diese Datei erneut starten"
              : "Textauswertung starten"
          }
        >
          {schickt
            ? "Wird erneut angestoßen …"
            : info
              ? "Erneut versuchen"
              : "Textauswertung starten"}
        </button>
      )}
      {info?.status === "completed" && text.length > 0 && (
        <>
          <button
            type="button"
            className="knopf-leise"
            aria-expanded={offen}
            aria-controls={vorschauId}
            onClick={() =>
              setInteraktion((vorher) => ({ ...vorher, offen: !offen }))
            }
          >
            {offen ? "Vorschau schließen" : "Textvorschau"}
          </button>
          {offen && (
            <div className="extraktion-vorschau" id={vorschauId}>
              {angaben.length > 0 && (
                <p className="extraktion-angaben">
                  Erkannt: {angaben.join(" · ")}
                </p>
              )}
              <p>
                {text.slice(0, vorschauLaenge)}
                {text.length > vorschauLaenge ? "…" : ""}
              </p>
            </div>
          )}
        </>
      )}
      {(titel || fehler === "laden") && (
        <p role="alert" className="feld-fehler">
          {titel || ladeFehlerText}
          {fehler === "laden" && (
            <>
              {" "}
              <button type="button" onClick={aktualisiereExtraktion}>
                Erneut versuchen
              </button>
            </>
          )}
        </p>
      )}
    </div>
  );
}
