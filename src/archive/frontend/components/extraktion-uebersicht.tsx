"use client";

import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useRef,
  useState,
} from "react";
import { type ExtraktionsInfo, holeExtraktionStatus } from "@/lib/extraktion";

type Stand = {
  info?: ExtraktionsInfo;
  geladen: boolean;
  fehler: "laden" | "verweigert" | null;
};

const leer: Stand = { geladen: false, fehler: null };
const ExtraktionKontext = createContext<{
  staende: Record<string, Stand>;
  registriere: (id: string) => () => void;
  aktualisiereExtraktion: () => void;
  setzeExtraktion: (info: ExtraktionsInfo) => void;
} | null>(null);

/** Eine gebündelte Nachfrage je Materialliste, nur für registrierte PDFs. */
export function ExtraktionUebersichtProvider({
  children,
}: {
  children: ReactNode;
}) {
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
  const aktualisiereExtraktion = useCallback(() => {
    setAbfrage((vorher) => vorher + 1);
  }, []);
  const setzeExtraktion = useCallback((info: ExtraktionsInfo) => {
    setStaende((vorher) => ({
      ...vorher,
      [info.revisionId]: { info, geladen: true, fehler: null },
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
        const results = await holeExtraktionStatus(ids, abbruch.signal);
        if (abbruch.signal.aborted) return;
        setStaende(
          Object.fromEntries(
            ids.map((id) => [
              id,
              {
                info: results.find((info) => info.revisionId === id),
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

  useEffect(() => {
    if (laedt) return;
    const laufend = schluessel.split(",").some((id) => {
      const stand = staende[id];
      return (
        !stand?.fehler &&
        (stand?.info?.status === "queued" || stand?.info?.status === "running")
      );
    });
    if (!laufend) return;
    const timer = window.setTimeout(aktualisiereExtraktion, 4000);
    return () => window.clearTimeout(timer);
  }, [staende, schluessel, aktualisiereExtraktion, laedt]);

  return (
    <ExtraktionKontext.Provider
      value={{ staende, registriere, aktualisiereExtraktion, setzeExtraktion }}
    >
      {children}
    </ExtraktionKontext.Provider>
  );
}

export function useExtraktionStand(revisionId: string, isEditor: boolean) {
  const kontext = useContext(ExtraktionKontext);
  if (!kontext) throw new Error("Auswertungsübersicht fehlt.");
  const { registriere } = kontext;
  useEffect(() => {
    if (!isEditor) return;
    return registriere(revisionId);
  }, [revisionId, isEditor, registriere]);
  return {
    ...(kontext.staende[revisionId] ?? leer),
    aktualisiereExtraktion: kontext.aktualisiereExtraktion,
    setzeExtraktion: kontext.setzeExtraktion,
  };
}
