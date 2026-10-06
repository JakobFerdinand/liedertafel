import Link from "next/link";
import type { HistorieZeile } from "@/lib/historie";
import {
  aufnahmePfad,
  bereichText,
  type HistorieAufnahme,
  stellenPfad,
} from "@/lib/zeitmarken";

// ARC-032: Aufnahmen, in denen eine Aufführung markiert ist, an der Zeile
// der Liedhistorie. Der Platz bleibt leer, wo nichts markiert ist: fehlende
// Markierung ist kein Beleg dafür, dass es keine Aufnahme gibt. Eine Marke,
// die an einer früheren Datei genommen wurde, bietet Mitgliedern keinen
// Sprung an, sondern nur die ganze Aufnahme.

function artName(art: HistorieAufnahme["kind"]): string {
  return art === "video" ? "Video" : "Tonaufnahme";
}

export function HistorieAufnahmen({
  zeile,
  liedTitel,
  isEditor,
}: {
  zeile: HistorieZeile;
  liedTitel: string;
  isEditor: boolean;
}) {
  const aufnahmen = zeile.recordings ?? [];
  if (aufnahmen.length === 0) return null;
  return (
    <ul className="historie-aufnahmen" aria-label="Aufnahmen dieser Aufführung">
      {aufnahmen.map((aufnahme) => {
        const pruefen = aufnahme.timestampState !== "current";
        const hatZeiten =
          aufnahme.startSeconds !== null && aufnahme.endSeconds !== null;
        // Die Redaktion darf eine ungeprüfte Marke anspringen, um sie zu prüfen.
        const springbar = hatZeiten && (!pruefen || isEditor);
        return (
          <li key={aufnahme.passageId} className="historie-aufnahme">
            <span>
              {artName(aufnahme.kind)} „{aufnahme.recordingLabel}“
              {isEditor && !aufnahme.isPublished && (
                <span className="noten-info"> · Entwurf</span>
              )}
              {hatZeiten && (
                <>
                  {" · "}
                  {bereichText(
                    aufnahme.startSeconds ?? 0,
                    aufnahme.endSeconds ?? 0,
                  )}
                </>
              )}
              {pruefen && (
                <span className="noten-info">
                  {isEditor
                    ? " · Zeitmarke zu prüfen"
                    : " · Zeitmarke wird überprüft"}
                </span>
              )}
            </span>
            {springbar ? (
              <Link
                href={stellenPfad(
                  zeile.eventId,
                  aufnahme.recordingId,
                  aufnahme.passageId,
                )}
                aria-label={`Zu „${liedTitel}“ in „${aufnahme.recordingLabel}“ springen`}
              >
                Zur Stelle
              </Link>
            ) : (
              <Link
                href={aufnahmePfad(zeile.eventId, aufnahme.recordingId)}
                aria-label={`Aufnahme „${aufnahme.recordingLabel}“ öffnen`}
              >
                Aufnahme öffnen
              </Link>
            )}
          </li>
        );
      })}
    </ul>
  );
}
