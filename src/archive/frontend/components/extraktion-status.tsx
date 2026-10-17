"use client";
import { useEffect, useRef, useState } from "react";
import { useExtraktionStand } from "@/components/extraktion-uebersicht";
import {
  extraktionsStatusText,
  type LeseStatus,
  leseRest,
  leseStatusText,
  notenAngabenTeile,
  starteExtraktionErneut,
  starteLesenErneut,
} from "@/lib/extraktion";
import { problemTitel } from "@/lib/uebertragung";

const vorschauLaenge = 4000;
const ladeFehlerText = "Auswertungsstatus konnte nicht geladen werden.";
const wiederholErsatz =
  "Der erneute Versuch konnte nicht gestartet werden. Bitte erneut versuchen.";
const lesenErsatz =
  "Das Lesen konnte nicht gestartet werden. Bitte erneut versuchen.";

export function ExtraktionStatus({
  dateiId,
  isEditor,
  angabenBekannt = true,
  kannLesen = false,
  bezeichnung = "diese Datei",
  onErkannt,
}: {
  dateiId: string;
  isEditor: boolean;
  /** Noten können von der KI gelesen werden; Auftrittsdokumente nicht. */
  kannLesen?: boolean;
  /** Benennt die Datei in den Knopfnamen, damit gleiche Knöpfe unterscheidbar sind. */
  bezeichnung?: string;
  /** Trägt der Eintrag die erkannten Angaben schon? */
  angabenBekannt?: boolean;
  /** Frisch erkannte Angaben liegen vor: der Eintrag soll neu laden. */
  onErkannt?: () => void;
}) {
  const { info, geladen, fehler, aktualisiereExtraktion, setzeExtraktion } =
    useExtraktionStand(dateiId, isEditor);
  const abbruchRef = useRef<AbortController | null>(null);
  const [interaktion, setInteraktion] = useState({
    dateiId,
    schickt: false,
    offen: false,
    titel: "",
    verweigert: false,
    lesenSchickt: false,
    lesenTitel: "",
  });
  const aktuell = interaktion.dateiId === dateiId;
  const schickt = aktuell && interaktion.schickt;
  const offen = aktuell && interaktion.offen;
  const titel = aktuell ? interaktion.titel : "";
  const lesenSchickt = aktuell && interaktion.lesenSchickt;
  const lesenTitel = aktuell ? interaktion.lesenTitel : "";

  useEffect(() => {
    setInteraktion({
      dateiId,
      schickt: false,
      offen: false,
      titel: "",
      verweigert: false,
      lesenSchickt: false,
      lesenTitel: "",
    });
    return () => {
      abbruchRef.current?.abort();
      abbruchRef.current = null;
    };
  }, [dateiId]);

  // Eine eben fertige Auswertung benennt den Eintrag ohne Neuladen der
  // Seite; je Datei höchstens eine Nachfrage.
  const gemeldetRef = useRef<string | null>(null);
  const neuErkannt =
    !angabenBekannt && info?.status === "completed" && Boolean(info.facts);
  useEffect(() => {
    if (!neuErkannt || gemeldetRef.current === dateiId) return;
    gemeldetRef.current = dateiId;
    onErkannt?.();
  }, [neuErkannt, dateiId, onErkannt]);

  // ARC-034-1: Hat die KI die Seiten eben fertig gelesen, hat sie vielleicht
  // Felder des Liedes gefüllt; der Eintrag lädt dann neu (frische Werte und
  // „KI“-Abzeichen). Nur der Wechsel von laufend auf fertig löst das aus.
  const leseStand: LeseStatus | null = info?.reading?.status ?? null;
  const vorherLeseRef = useRef<{ id: string; status: LeseStatus | null }>({
    id: dateiId,
    status: null,
  });
  useEffect(() => {
    const vorher = vorherLeseRef.current;
    vorherLeseRef.current = { id: dateiId, status: leseStand };
    if (
      vorher.id === dateiId &&
      (vorher.status === "queued" || vorher.status === "running") &&
      leseStand === "completed"
    ) {
      onErkannt?.();
    }
  }, [leseStand, dateiId, onErkannt]);

  const lesenRef = useRef<HTMLOutputElement>(null);
  const [fokusLesen, setFokusLesen] = useState(false);
  // Die Statuszeile erscheint erst mit der Antwort: dann geht der Fokus hinein.
  const lesenDa = info?.readingEnabled === true && Boolean(info.reading);
  useEffect(() => {
    if (fokusLesen && lesenDa && lesenRef.current) {
      lesenRef.current.focus();
      setFokusLesen(false);
    }
  }, [fokusLesen, lesenDa]);
  async function lesenAnstossen(aktion: "retry" | "continue") {
    if (abbruchRef.current) return;
    const abbruch = new AbortController();
    abbruchRef.current = abbruch;
    setInteraktion((vorher) => ({
      ...vorher,
      lesenSchickt: true,
      lesenTitel: "",
    }));
    try {
      const reading = await (aktion === "retry" ? starteLesenErneut : leseRest)(
        dateiId,
        abbruch.signal,
      );
      if (abbruch.signal.aborted) return;
      if (info) setzeExtraktion({ ...info, reading });
      // Der Knopf verschwindet: der Fokus geht in die Statuszeile.
      setFokusLesen(true);
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
      const serverTitel = await problemTitel(ursache, lesenErsatz);
      if (abbruch.signal.aborted) return;
      setInteraktion((vorher) => ({ ...vorher, lesenTitel: serverTitel }));
      aktualisiereExtraktion();
    } finally {
      if (!abbruch.signal.aborted) {
        abbruchRef.current = null;
        setInteraktion((vorher) => ({ ...vorher, lesenSchickt: false }));
      }
    }
  }

  async function erneutStarten() {
    if (abbruchRef.current) return;
    const abbruch = new AbortController();
    abbruchRef.current = abbruch;
    setInteraktion((vorher) => ({ ...vorher, schickt: true, titel: "" }));
    try {
      const neu = await starteExtraktionErneut(dateiId, abbruch.signal);
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
  const vorschauId = `extraktion-vorschau-${dateiId}`;
  // Ist das KI-Lesen auf dem Server nicht eingerichtet, bleibt die Zeile bei
  // der Textauswertung: weder Stand noch Knöpfe.
  const lesenAn = info?.readingEnabled === true;
  const lesen = lesenAn ? (info?.reading ?? null) : null;
  const lesenGrund =
    lesen?.status === "failed" ? (lesen.failureReason?.trim() ?? "") : "";
  // Eine fertige Auswertung ohne Lesezeile (Datei aus der Zeit davor) kann
  // die Redaktion von der KI lesen lassen: Scans Seite für Seite, Texte nur
  // für die noch leeren Felder.
  const lesenAngebot =
    kannLesen &&
    lesenAn &&
    !fehler &&
    info &&
    !lesen &&
    (info.status === "noText" || info.status === "completed");

  return (
    <div className="extraktion-status">
      {info && (
        <output className="extraktion-stand">
          {extraktionsStatusText(info.status)}
          {grund ? <span className="extraktion-grund"> — {grund}</span> : null}
        </output>
      )}
      {lesen && (
        <output className="extraktion-lesen" ref={lesenRef} tabIndex={-1}>
          {leseStatusText(lesen)}
          {lesenGrund ? (
            <span className="extraktion-grund"> — {lesenGrund}</span>
          ) : null}
        </output>
      )}
      {lesenAngebot && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void lesenAnstossen("retry")}
          disabled={lesenSchickt}
          aria-label={
            info.status === "noText"
              ? `Seiten von der KI lesen: ${bezeichnung}`
              : `Angaben von der KI ergänzen: ${bezeichnung}`
          }
        >
          {lesenSchickt
            ? "Wird angestoßen …"
            : info.status === "noText"
              ? "Seiten von der KI lesen"
              : "Angaben von der KI ergänzen"}
        </button>
      )}
      {lesen?.status === "failed" && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void lesenAnstossen("retry")}
          disabled={lesenSchickt}
          aria-label={`Erneut lesen: ${bezeichnung} von der KI`}
        >
          {lesenSchickt ? "Wird erneut angestoßen …" : "Erneut lesen"}
        </button>
      )}
      {lesen?.status === "completed" && lesen.partial && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void lesenAnstossen("continue")}
          disabled={lesenSchickt}
          aria-label={`Rest lesen: restliche Seiten von ${bezeichnung}`}
        >
          {lesenSchickt ? "Wird angestoßen …" : "Rest lesen"}
        </button>
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
              {info.textSource === "reading" && (
                <p className="extraktion-angaben">
                  Von der KI aus den Seiten des Scans gelesen.
                </p>
              )}
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
      {(titel || lesenTitel || fehler === "laden") && (
        <p role="alert" className="feld-fehler">
          {titel || lesenTitel || ladeFehlerText}
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
