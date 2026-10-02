"use client";

import { useEffect, useRef, useState } from "react";
import { useExtraktionStand } from "@/components/extraktion-uebersicht";
import { problemTitel } from "@/lib/assets";
import {
  extraktionsStatusText,
  starteExtraktionErneut,
} from "@/lib/extraktion";

const vorschauLaenge = 600;
const ladeFehlerText = "Auswertungsstatus konnte nicht geladen werden.";
const wiederholErsatz =
  "Der erneute Versuch konnte nicht gestartet werden. Bitte erneut versuchen.";

export function ExtraktionStatus({
  revisionId,
  isEditor,
}: {
  revisionId: string;
  isEditor: boolean;
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
  const text = typeof info?.text === "string" ? info.text : "";
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
            <p className="extraktion-vorschau" id={vorschauId}>
              {text.slice(0, vorschauLaenge)}
              {text.length > vorschauLaenge ? "…" : ""}
            </p>
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
