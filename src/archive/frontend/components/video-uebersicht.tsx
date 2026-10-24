"use client";

// #161: Der Stand der automatischen MP4-Abspielkopie am Video-Eintrag. Die
// Redaktion sieht Fortschritt und Fehlgründe und kann nach einem Fehlschlag
// die Konvertierung erneut starten; die Bündelung der Nachfrage folgt der
// Rezeptur extraktion-uebersicht.tsx.

import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";
import { problemTitel } from "@/lib/uebertragung";
import {
  holeVideoStatus,
  konvertierungLaeuft,
  starteKonvertierungErneut,
  type VideoInfo,
  videoStatusText,
} from "@/lib/videos";

type Stand = {
  info?: VideoInfo;
  geladen: boolean;
  fehler: "laden" | "verweigert" | null;
};

const leer: Stand = { geladen: false, fehler: null };
const wiederholErsatz =
  "Der erneute Versuch konnte nicht gestartet werden. Bitte erneut versuchen.";
const VideoKontext = createContext<{
  staende: Record<string, Stand>;
  registriere: (id: string) => () => void;
  aktualisiereVideos: () => void;
  setzeVideo: (info: VideoInfo) => void;
} | null>(null);

/** Eine gebündelte Nachfrage je Materialliste, nur für registrierte Videos. */
export function VideoUebersichtProvider({ children }: { children: ReactNode }) {
  const registrierungen = useRef<Record<string, number>>({});
  const [schluessel, setSchluessel] = useState("");
  const [staende, setStaende] = useState<Record<string, Stand>>({});
  const [abfrage, setAbfrage] = useState(0);
  const [laedt, setLaedt] = useState(false);
  const registriere = useCallback((id: string) => {
    const zaehler = registrierungen.current;
    zaehler[id] = (zaehler[id] ?? 0) + 1;
    setSchluessel(Object.keys(zaehler).sort().join(","));
    return () => {
      if (zaehler[id] > 1) {
        zaehler[id] -= 1;
        return;
      }
      delete zaehler[id];
      setSchluessel(Object.keys(zaehler).sort().join(","));
      setStaende((vorher) => {
        const neu = { ...vorher };
        delete neu[id];
        return neu;
      });
    };
  }, []);
  const aktualisiereVideos = useCallback(() => {
    setAbfrage((vorher) => vorher + 1);
  }, []);
  const setzeVideo = useCallback((info: VideoInfo) => {
    setStaende((vorher) => ({
      ...vorher,
      [info.dateiId]: { info, geladen: true, fehler: null },
    }));
  }, []);

  // Der kurze Aufschub bündelt die Registrierungen aller Einträge. Jeder
  // Wechsel bricht die alte Anfrage ab; späte Antworten bleiben wirkungslos.
  // biome-ignore lint/correctness/useExhaustiveDependencies: abfrage stößt denselben Stapelabruf erneut an.
  useEffect(() => {
    if (!schluessel) {
      setLaedt(false);
      return;
    }
    setLaedt(true);
    const ids = schluessel.split(",");
    const abbruch = new AbortController();
    async function holen() {
      try {
        const results = await holeVideoStatus(ids, abbruch.signal);
        if (abbruch.signal.aborted) return;
        setStaende(
          Object.fromEntries(
            ids.map((id) => [
              id,
              {
                info: results.find((info) => info.dateiId === id),
                geladen: true,
                fehler: null,
              },
            ]),
          ),
        );
      } catch (ursache) {
        if (abbruch.signal.aborted) return;
        const verweigert =
          ursache instanceof Response &&
          (ursache.status === 401 || ursache.status === 403);
        setStaende((vorher) =>
          Object.fromEntries(
            ids.map((id) => [
              id,
              {
                info: verweigert ? undefined : vorher[id]?.info,
                geladen: true,
                fehler: verweigert ? "verweigert" : "laden",
              },
            ]),
          ),
        );
      } finally {
        if (!abbruch.signal.aborted) setLaedt(false);
      }
    }
    const timer = window.setTimeout(() => void holen(), 30);
    return () => {
      window.clearTimeout(timer);
      abbruch.abort();
    };
  }, [schluessel, abfrage]);

  // Solange eine Konvertierung wartet oder läuft, fragt die Liste weiter.
  useEffect(() => {
    if (laedt) return;
    const laeuft = schluessel
      .split(",")
      .some((id) =>
        staende[id]?.fehler ? false : konvertierungLaeuft(staende[id]?.info),
      );
    if (!laeuft) return;
    const timer = window.setTimeout(aktualisiereVideos, 4000);
    return () => window.clearTimeout(timer);
  }, [staende, schluessel, aktualisiereVideos, laedt]);

  return (
    <VideoKontext.Provider
      value={{ staende, registriere, aktualisiereVideos, setzeVideo }}
    >
      {children}
    </VideoKontext.Provider>
  );
}

/** Stand der Konvertierung einer Datei; verlangt die Übersicht über der Liste. */
function useVideoStand(dateiId: string, isEditor: boolean) {
  const kontext = useContext(VideoKontext);
  if (!kontext) throw new Error("Videoübersicht fehlt.");
  const { registriere } = kontext;
  useEffect(() => {
    if (!isEditor) return;
    return registriere(dateiId);
  }, [dateiId, isEditor, registriere]);
  return {
    ...(kontext.staende[dateiId] ?? leer),
    aktualisiereVideos: kontext.aktualisiereVideos,
    setzeVideo: kontext.setzeVideo,
  };
}

/** Ist die Datei Video-Aufnahmematerial (Vertrag VideoService)? */
/**
 * Die Konvertierungszeile des Video-Eintrags: nur für die Redaktion. Ein
 * gerade fertiger Lauf meldet sich über `onAbgeschlossen` — die Liste lädt
 * dann neu, damit die Kopie bereits mit dem nächsten Stand sichtbar ist.
 */
export function VideoKonvertierung({
  dateiId,
  isEditor,
  bezeichnung,
  onAbgeschlossen,
}: {
  dateiId: string;
  isEditor: boolean;
  /** Benennt die Datei in den Knopfnamen, damit gleiche Knöpfe unterscheidbar sind. */
  bezeichnung: string;
  /** Die Abspielkopie ist eben fertig: der Eintrag soll neu laden. */
  onAbgeschlossen?: () => void;
}) {
  const stand = useVideoStand(dateiId, isEditor);
  const abbruchRef = useRef<AbortController | null>(null);
  const [schickt, setSchickt] = useState(false);
  const [titel, setTitel] = useState("");

  // Nur der Übergang von wartend/laufend auf fertig löst das Neuladen aus.
  const status = stand?.info?.status ?? null;
  const vorherRef = useRef<{ id: string; status: string | null }>({
    id: dateiId,
    status: null,
  });
  useEffect(() => {
    const vorher = vorherRef.current;
    vorherRef.current = { id: dateiId, status };
    if (
      vorher.id === dateiId &&
      (vorher.status === "queued" || vorher.status === "running") &&
      status === "completed"
    ) {
      onAbgeschlossen?.();
    }
  }, [status, dateiId, onAbgeschlossen]);

  useEffect(() => {
    const stand = abbruchRef.current;
    return () => {
      stand?.abort();
      if (abbruchRef.current === stand) abbruchRef.current = null;
    };
  }, []);

  async function erneutStarten() {
    if (abbruchRef.current) return;
    const abbruch = new AbortController();
    abbruchRef.current = abbruch;
    setSchickt(true);
    setTitel("");
    try {
      const neu = await starteKonvertierungErneut(dateiId, abbruch.signal);
      if (abbruch.signal.aborted) return;
      stand.setzeVideo(neu);
      stand.aktualisiereVideos();
    } catch (ursache) {
      if (abbruch.signal.aborted) return;
      if (
        ursache instanceof Response &&
        (ursache.status === 401 || ursache.status === 403)
      ) {
        return;
      }
      const serverTitel = await problemTitel(ursache, wiederholErsatz);
      if (abbruch.signal.aborted) return;
      setTitel(serverTitel);
      stand.aktualisiereVideos();
    } finally {
      if (!abbruch.signal.aborted) {
        abbruchRef.current = null;
        setSchickt(false);
      }
    }
  }

  if (!isEditor || stand.fehler === "verweigert") return null;
  const info = stand.info;
  const grund =
    info?.status === "failed" ? (info.failureReason?.trim() ?? "") : "";

  return (
    <div className="extraktion-status">
      {stand.fehler === "laden" ? (
        <output className="extraktion-stand">
          Der Konvertierungsstand konnte nicht geladen werden.
        </output>
      ) : null}
      {info && (
        <output className="extraktion-stand">
          {videoStatusText(info)}
          {grund ? <span className="extraktion-grund"> — {grund}</span> : null}
        </output>
      )}
      {(!info || info.status === "failed") && (
        <button
          type="button"
          className="knopf-leise"
          onClick={() => void erneutStarten()}
          disabled={schickt}
          aria-label={
            info
              ? `Abspielkopie von ${bezeichnung} erneut erstellen`
              : `Abspielkopie von ${bezeichnung} anstoßen`
          }
        >
          {schickt
            ? "Wird erneut angestoßen …"
            : info
              ? "Erneut versuchen"
              : "Abspielkopie erstellen"}
        </button>
      )}
      {titel && (
        <p role="alert" className="feld-fehler">
          {titel}
        </p>
      )}
    </div>
  );
}
