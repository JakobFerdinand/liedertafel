"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { groesseText } from "@/lib/assets";
import { fetchMe, type MeResponse } from "@/lib/auth";
import { fetchPapierkorb, type PapierkorbEintrag } from "@/lib/dateien";

const artText: Record<string, string> = {
  noten: "Noten",
  uebe: "Übe-Datei",
  aufnahme: "Aufnahme",
  sonstiges: "Sonstiges",
};

function datumText(wert: string): string {
  return new Date(wert).toLocaleString("de-AT", {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

function istRedaktion(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

/**
 * Papierkorb der Redaktion (#168): gelöschte und ersetzte Dateien, nur zum
 * Ansehen. Wiederherstellen und endgültiges Entfernen folgen mit #146.
 */
export function Papierkorb() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [eintraege, setEintraege] = useState<PapierkorbEintrag[] | null>(null);
  const [fehler, setFehler] = useState("");

  useEffect(() => {
    const abbruch = new AbortController();
    (async () => {
      const aktuell = await fetchMe(abbruch.signal);
      setMe(aktuell);
      if (istRedaktion(aktuell)) {
        setEintraege(await fetchPapierkorb(abbruch.signal));
      }
    })().catch(() => {
      if (!abbruch.signal.aborted) {
        setFehler("Der Papierkorb konnte nicht geladen werden.");
      }
    });
    return () => abbruch.abort();
  }, []);

  if (fehler) return <p className="feld-fehler">{fehler}</p>;
  if (me === null) return <p aria-live="polite">Papierkorb wird geladen …</p>;
  if (!istRedaktion(me)) {
    return <p>Der Papierkorb ist nur für die Redaktion sichtbar.</p>;
  }
  if (eintraege === null) {
    return <p aria-live="polite">Papierkorb wird geladen …</p>;
  }
  if (eintraege.length === 0) {
    return <p className="noten-info">Der Papierkorb ist leer.</p>;
  }
  return (
    <ul className="papierkorb-liste" aria-label="Gelöschte Dateien">
      {eintraege.map((eintrag) => (
        <li className="material-eintrag" key={eintrag.id}>
          <p className="material-stimme">{eintrag.fileName ?? "Datei"}</p>
          <p className="noten-info">
            {artText[eintrag.kind] ?? eintrag.kind}
            {eintrag.stimme ? ` · ${eintrag.stimme}` : ""}
            {eintrag.isPartitur ? " · Partitur" : ""} ·{" "}
            {groesseText(eintrag.sizeBytes)}
          </p>
          <p className="noten-info">
            {eintrag.liedId ? (
              <Link href={`/lied/?id=${eintrag.liedId}`}>
                {eintrag.liedTitle}
              </Link>
            ) : (
              (eintrag.eventTitle ?? "Auftritt")
            )}
          </p>
          <p className="noten-info">
            {eintrag.replaced ? "Ersetzt" : "Gelöscht"} am{" "}
            {datumText(eintrag.deletedAt)}
            {eintrag.deletedBy ? ` von ${eintrag.deletedBy}` : ""}
          </p>
        </li>
      ))}
    </ul>
  );
}
