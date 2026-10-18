"use client";

import Link from "next/link";
import { useState } from "react";
import { AudioSpieler } from "@/components/audio-spieler";
import { MedienSpieler } from "@/components/medien-spieler";
import { MidiSpieler } from "@/components/midi-spieler";
import { type DateiZugriff, fetchDateiZugriff } from "@/lib/dateien";
import { dateiTitel, type LiedDatei } from "@/lib/lieder";
import { groesseText } from "@/lib/uebertragung";

// Übe-Dateien und Aufnahmen eines Liedes (#167): holt den Zugriff erst auf
// Klick und spielt Ton, MIDI (mit Tempo) oder Video auf den gemeinsamen
// Spielern. Die Spieler fragen über `fetchDateiZugriff` selbst Tickets nach.

function spielart(datei: LiedDatei): "midi" | "video" | "audio" {
  if (datei.contentType.includes("midi")) return "midi";
  if (datei.contentType.startsWith("video/")) return "video";
  return "audio";
}

export function DateiSpieler({
  datei,
  aktiv,
  onAbspielen,
}: {
  datei: LiedDatei;
  /** Nur ein Spieler läuft gleichzeitig. */
  aktiv: boolean;
  onAbspielen: () => void;
}) {
  const [zugriff, setZugriff] = useState<DateiZugriff | null>(null);
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");
  const art = spielart(datei);
  const name = dateiTitel(datei);

  async function vorbereiten() {
    setBusy(true);
    setFehler("");
    try {
      setZugriff(await fetchDateiZugriff(datei.id));
    } catch {
      setFehler(
        "Die Datei konnte nicht geladen werden. Bitte erneut versuchen.",
      );
    } finally {
      setBusy(false);
    }
  }

  const holeZugriff = () => fetchDateiZugriff(datei.id);
  const auftritt = datei.recordedAtEvent;

  return (
    <article
      className="material-eintrag"
      aria-label={`${datei.kind === "aufnahme" ? "Aufnahme" : "Übe-Datei"} · ${name}`}
    >
      <p className="material-stimme">{name}</p>
      <p className="noten-info">{groesseText(datei.sizeBytes)}</p>
      {datei.description && <p className="noten-info">{datei.description}</p>}
      {auftritt && (
        <p className="noten-info">
          Aufgenommen bei{" "}
          <Link href={`/auftritt/?id=${encodeURIComponent(auftritt.id)}`}>
            {auftritt.title}
          </Link>
          {auftritt.dateDisplay ? ` (${auftritt.dateDisplay})` : ""}
        </p>
      )}
      <div className="noten-aktionen">
        {zugriff ? (
          <>
            {art === "midi" ? (
              <MidiSpieler
                dateiId={datei.id}
                stimme={name}
                zugriff={zugriff}
                holeZugriff={holeZugriff}
                aktiv={aktiv}
                onAbspielen={onAbspielen}
                onErneuert={setZugriff}
              />
            ) : art === "video" ? (
              <MedienSpieler
                kennung={datei.id}
                art="video"
                name={name}
                zugriff={zugriff}
                holeZugriff={holeZugriff}
                aktiv={aktiv}
                onAbspielen={onAbspielen}
                onErneuert={setZugriff}
                ladeMeldung="Video konnte nicht geladen werden. Bitte erneut versuchen."
                herunterladenErlaubt
                formatMeldung="Dieses Videoformat kann im Browser nicht wiedergegeben werden. Die Datei kann weiterhin heruntergeladen werden."
              />
            ) : (
              <AudioSpieler
                dateiId={datei.id}
                stimme={name}
                zugriff={zugriff}
                holeZugriff={holeZugriff}
                aktiv={aktiv}
                onAbspielen={onAbspielen}
                onErneuert={setZugriff}
              />
            )}
            <a className="noten-laden" href={zugriff.downloadUrl} download>
              Herunterladen
            </a>
          </>
        ) : (
          <button
            type="button"
            onClick={() => void vorbereiten()}
            disabled={busy}
          >
            {busy
              ? "Wird vorbereitet …"
              : art === "video"
                ? "Ansehen"
                : "Anhören"}
          </button>
        )}
      </div>
      {fehler && (
        <output aria-live="polite" className="feld-fehler">
          {fehler}
        </output>
      )}
    </article>
  );
}
