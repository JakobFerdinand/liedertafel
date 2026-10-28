"use client";

// #148: Die Cold-Storage-Zeile des Eintrags. Die Redaktion sieht, auf welcher
// Stufe das Original liegt, und verschiebt es ins Cold Storage, sobald
// Mitglieder es über eine eigene Abspielkopie erreichen — und zurück, falls es
// wieder online das Material selbst stellen muss. Die Verschiebung bietet die
// Zeile nur an, wenn eine separate Abspielkopie existiert (derselbe Vertrag,
// den der Server mit 422 durchsetzt); ein Fehlschlag bleibt als deutscher
// Fehler sichtbar, und die Zeile behauptet nie einen Erfolg, der nicht
// eingetreten ist (der Server meldet die Stufe erst nach der Verschiebung).

import { useEffect, useRef, useState } from "react";
import {
  type SpeicherTierZiel,
  speicherTierText,
  verschiebeSpeicherTier,
} from "@/lib/speicher";
import { problemTitel } from "@/lib/uebertragung";

const verschiebeErsatz =
  "Die Verschiebung ist fehlgeschlagen. Bitte erneut versuchen.";

export function SpeicherTier({
  dateiId,
  storageTier,
  hatAbspielkopie,
  rowVersion,
  tierChangedAt,
  bezeichnung,
  onGeaendert,
}: {
  dateiId: string;
  /** "hot" oder "cold" (Vertrag DateiPayloads.Summary bzw. SlotPayload). */
  storageTier: string;
  /** Eine separate, live Abspielkopie steht bereit (Vertrag des Server-Guards). */
  hatAbspielkopie: boolean;
  /** Stand der Datei; mitgeschickt, damit ein veralteter Eintrag 409 antwortet. */
  rowVersion?: number;
  /** Wann die Stufe zuletzt gewechselt hat (Vertrag DateiPayloads.Summary). */
  tierChangedAt?: string | null;
  /** Benennt die Datei im Knopf, damit gleiche Knöpfe unterscheidbar sind. */
  bezeichnung: string;
  /** Nach gelungener Verschiebung: die Liste lädt neu, damit der Stand stimmt. */
  onGeaendert?: () => void;
}) {
  const abbruchRef = useRef<AbortController | null>(null);
  const [schickt, setSchickt] = useState(false);
  const [titel, setTitel] = useState("");

  useEffect(() => {
    const laufend = abbruchRef.current;
    return () => {
      laufend?.abort();
      if (abbruchRef.current === laufend) abbruchRef.current = null;
    };
  }, []);

  async function verschieben(tier: SpeicherTierZiel) {
    if (abbruchRef.current) return;
    const abbruch = new AbortController();
    abbruchRef.current = abbruch;
    setSchickt(true);
    setTitel("");
    try {
      await verschiebeSpeicherTier(dateiId, tier, rowVersion, abbruch.signal);
      if (abbruch.signal.aborted) return;
      onGeaendert?.();
    } catch (ursache) {
      if (abbruch.signal.aborted) return;
      if (
        ursache instanceof Response &&
        (ursache.status === 401 || ursache.status === 403)
      ) {
        return;
      }
      const serverTitel = await problemTitel(ursache, verschiebeErsatz);
      if (abbruch.signal.aborted) return;
      setTitel(serverTitel);
    } finally {
      if (!abbruch.signal.aborted) {
        abbruchRef.current = null;
        setSchickt(false);
      }
    }
  }

  const kalt = storageTier === "cold";
  if (!kalt && !hatAbspielkopie) return null;
  return (
    <div className="extraktion-status">
      <output className="extraktion-stand">
        {speicherTierText(kalt ? "cold" : "hot", tierChangedAt)}
      </output>
      <button
        type="button"
        className="knopf-leise"
        onClick={() => void verschieben(kalt ? "hot" : "cold")}
        disabled={schickt}
        aria-label={
          kalt
            ? `${bezeichnung} ins Hot Storage zurückholen`
            : `${bezeichnung} ins Cold Storage verschieben`
        }
      >
        {schickt
          ? kalt
            ? "Wird zurückgeholt …"
            : "Wird verschoben …"
          : kalt
            ? "Ins Hot Storage zurückholen"
            : "Ins Cold Storage verschieben"}
      </button>
      {titel && (
        <p role="alert" className="feld-fehler">
          {titel}
        </p>
      )}
    </div>
  );
}
