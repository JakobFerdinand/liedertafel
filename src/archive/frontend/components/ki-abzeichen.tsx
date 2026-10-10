"use client";

import { useState } from "react";
import { zurueckholenFeld } from "@/lib/provenanz";
import type { FeldHerkunft } from "@/lib/songs";

/**
 * ARC-013-1: das "KI"-Abzeichen eines Feldes, das ein automatischer Lauf
 * geschrieben hat, samt dem Knopf zum Zurückholen des früheren Wertes. Nur
 * in Redaktionsformularen zu sehen; Mitglieder sehen nie ein Abzeichen.
 */
export function KiAbzeichen({
  herkunft,
  feldbezeichnung,
  zurueckgeholt,
  meldungsziel,
}: {
  herkunft?: FeldHerkunft;
  feldbezeichnung: string;
  /** Nach dem Zurückholen: die Detailansicht lädt neu. */
  zurueckgeholt?: () => void;
  /** Statusregion des Formulars für die Meldung. */
  meldungsziel?: (text: string) => void;
}) {
  const [busy, setBusy] = useState(false);
  if (!herkunft || herkunft.source === "Human") return null;
  const quelle = herkunft.source === "Ai" ? "KI" : "Automatische Auswertung";
  const sicherheit =
    herkunft.confidence === "Sicher"
      ? "als sicher gelesen"
      : "als unsicher gelesen";
  const zielTyp = herkunft.entityType;
  const zielId = herkunft.entityId;
  const zielFeld = herkunft.field;

  async function zurueckholen() {
    setBusy(true);
    try {
      const antwort = await zurueckholenFeld(zielTyp, zielId, zielFeld);
      const inhalt = (await antwort.json().catch(() => null)) as {
        title?: string;
      } | null;
      if (!antwort.ok) {
        meldungsziel?.(
          inhalt?.title ??
            "Das Zurückholen hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      meldungsziel?.(
        `Früheren Wert für „${feldbezeichnung}“ zurückgeholt. Das Feld ist gesichert.`,
      );
      zurueckgeholt?.();
    } catch {
      meldungsziel?.(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <span className="ki-abzeichen">
      <span
        className={
          herkunft.source === "Ai"
            ? "ki-abzeichen-marke"
            : "ki-abzeichen-marke ki-abzeichen-auswertung"
        }
        title={`${quelle} hat dieses Feld ${sicherheit} geschrieben.`}
      >
        {quelle === "KI" ? "KI" : "Auswertung"}
      </span>
      <span className="visually-hidden">
        ({feldbezeichnung}: von {quelle} {sicherheit})
      </span>
      <button
        type="button"
        className="ki-zurueckholen"
        onClick={zurueckholen}
        disabled={busy}
        aria-label={`KI-Wert für ${feldbezeichnung} zurückholen`}
      >
        {busy ? "Wird zurückgeholt …" : "Zurückholen"}
      </button>
    </span>
  );
}

/** Findet die automatische Herkunft eines Einzelfeldes in den gespeicherten Zeilen. */
export function feldHerkunft(
  herkunft: FeldHerkunft[] | undefined,
  entityType: "song" | "arrangement" | "musical_version",
  entityId: string,
  field: string,
): FeldHerkunft | undefined {
  const zeile = herkunft?.find(
    (eintrag) =>
      eintrag.entityType === entityType &&
      eintrag.entityId === entityId &&
      eintrag.field === field,
  );
  // Ein gesichertes Feld (letzte Herkunft Mensch) zeigt kein Abzeichen mehr.
  return zeile && zeile.source !== "Human" ? zeile : undefined;
}
