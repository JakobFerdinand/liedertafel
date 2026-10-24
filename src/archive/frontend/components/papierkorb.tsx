"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { fetchMe, type MeResponse } from "@/lib/auth";
import { fetchPapierkorb, type PapierkorbInhalt } from "@/lib/dateien";
import { auftrittArtName } from "@/lib/events";
import type { PapierkorbArt } from "@/lib/papierkorb";
import { entferneEndgueltig, stelleWiederHer } from "@/lib/papierkorb";
import { groesseText, problemMeldung } from "@/lib/uebertragung";

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

function istVerwaltung(me: MeResponse): boolean {
  return me.authenticated && me.roles.includes("Administrator");
}

const stellWiederHerFehler: Record<PapierkorbArt, string> = {
  lieder: "Das Lied konnte nicht wiederhergestellt werden.",
  auftritte: "Der Auftritt konnte nicht wiederhergestellt werden.",
  dateien: "Die Datei konnte nicht wiederhergestellt werden.",
};

/**
 * Papierkorb der Redaktion (#146): gelöschte und ersetzte Dateien, Lieder und
 * Auftritte, nur für die Redaktion. Wiederherstellen kann die ganze Redaktion;
 * das endgültige Entfernen bleibt der Verwaltung (Administrator).
 */
export function Papierkorb() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [inhalt, setInhalt] = useState<PapierkorbInhalt | null>(null);
  const [fehler, setFehler] = useState("");
  const [beschaftigt, setBeschaftigt] = useState<string | null>(null);
  // „art:id“ des Eintrags, dessen endgültiges Entfernen gerade bestätigt wird.
  const [bestaetigend, setBestaetigend] = useState<string | null>(null);
  const [aktionsFehler, setAktionsFehler] = useState<string | null>(null);

  useEffect(() => {
    const abbruch = new AbortController();
    (async () => {
      const aktuell = await fetchMe(abbruch.signal);
      setMe(aktuell);
      if (istRedaktion(aktuell)) {
        setInhalt(await fetchPapierkorb(abbruch.signal));
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
  if (inhalt === null) {
    return <p aria-live="polite">Papierkorb wird geladen …</p>;
  }

  const verwaltung = istVerwaltung(me);

  const entferne = (art: PapierkorbArt, id: string) =>
    setInhalt((bisher) =>
      bisher
        ? {
            dateien:
              art === "dateien"
                ? bisher.dateien.filter((e) => e.id !== id)
                : bisher.dateien,
            lieder:
              art === "lieder"
                ? bisher.lieder.filter((e) => e.id !== id)
                : bisher.lieder,
            auftritte:
              art === "auftritte"
                ? bisher.auftritte.filter((e) => e.id !== id)
                : bisher.auftritte,
          }
        : bisher,
    );

  // #146: Wiederherstellen beansprucht den Eintrag aus dem Papierkorb zurück;
  // eine Ablehnung des Archivs (Frist abgelaufen, Besitzer noch im Papierkorb)
  // zeigt ihren deutschen ProblemDetails-Titel.
  async function zurueckholen(art: PapierkorbArt, id: string) {
    setBeschaftigt(`${art}:${id}`);
    setAktionsFehler(null);
    try {
      await stelleWiederHer(art, id);
      entferne(art, id);
    } catch (ursache) {
      setAktionsFehler(
        await problemMeldung(ursache, stellWiederHerFehler[art]),
      );
    } finally {
      setBeschaftigt(null);
    }
  }

  // Nur die Verwaltung darf einen Eintrag endgültig entfernen; die Bestätigung
  // schützt vor dem Versehen.
  async function endgueltigEntfernen(art: PapierkorbArt, id: string) {
    setBeschaftigt(`${art}:${id}`);
    setAktionsFehler(null);
    try {
      await entferneEndgueltig(art, id);
      entferne(art, id);
    } catch (ursache) {
      setAktionsFehler(
        await problemMeldung(
          ursache,
          "Der Eintrag konnte nicht endgültig entfernt werden.",
        ),
      );
    } finally {
      setBeschaftigt(null);
      setBestaetigend(null);
    }
  }

  const leer =
    inhalt.dateien.length + inhalt.lieder.length + inhalt.auftritte.length ===
    0;
  if (leer) return <p className="noten-info">Der Papierkorb ist leer.</p>;

  const entferneKnopf = (art: PapierkorbArt, id: string, name: string) => {
    if (!verwaltung) return null;
    const schluessel = `${art}:${id}`;
    if (bestaetigend === schluessel) {
      return (
        <>
          <button
            type="button"
            className="knopf-leise"
            disabled={beschaftigt !== null}
            onClick={() => void endgueltigEntfernen(art, id)}
            aria-label={`Ja, endgültig entfernen · ${name}`}
          >
            Ja, endgültig entfernen
          </button>
          <button
            type="button"
            className="knopf-leise"
            disabled={beschaftigt !== null}
            onClick={() => setBestaetigend(null)}
          >
            Abbrechen
          </button>
        </>
      );
    }
    return (
      <button
        type="button"
        className="knopf-leise"
        disabled={beschaftigt !== null}
        onClick={() => setBestaetigend(schluessel)}
        aria-label={`Endgültig entfernen · ${name}`}
      >
        Endgültig entfernen
      </button>
    );
  };

  return (
    <>
      {aktionsFehler && (
        <output aria-live="polite" className="feld-fehler">
          {aktionsFehler}
        </output>
      )}
      {inhalt.lieder.length > 0 && (
        <section aria-labelledby="papierkorb-lieder-titel">
          <h2 id="papierkorb-lieder-titel">Lieder</h2>
          <ul className="papierkorb-liste" aria-label="Gelöschte Lieder">
            {inhalt.lieder.map((eintrag) => (
              <li className="material-eintrag" key={eintrag.id}>
                <p className="material-stimme">{eintrag.title}</p>
                <p className="noten-info">
                  {datumText(eintrag.deletedAt)}
                  {eintrag.deletedBy ? ` von ${eintrag.deletedBy}` : ""}
                </p>
                <button
                  type="button"
                  className="knopf-leise"
                  disabled={beschaftigt !== null}
                  onClick={() => void zurueckholen("lieder", eintrag.id)}
                  aria-label={`Wiederherstellen · ${eintrag.title}`}
                >
                  Wiederherstellen
                </button>
                {entferneKnopf("lieder", eintrag.id, eintrag.title)}
              </li>
            ))}
          </ul>
        </section>
      )}
      {inhalt.auftritte.length > 0 && (
        <section aria-labelledby="papierkorb-auftritte-titel">
          <h2 id="papierkorb-auftritte-titel">Auftritte</h2>
          <ul className="papierkorb-liste" aria-label="Gelöschte Auftritte">
            {inhalt.auftritte.map((eintrag) => (
              <li className="material-eintrag" key={eintrag.id}>
                <p className="material-stimme">{eintrag.title}</p>
                <p className="noten-info">{auftrittArtName(eintrag.kind)}</p>
                <p className="noten-info">
                  {datumText(eintrag.deletedAt)}
                  {eintrag.deletedBy ? ` von ${eintrag.deletedBy}` : ""}
                </p>
                <button
                  type="button"
                  className="knopf-leise"
                  disabled={beschaftigt !== null}
                  onClick={() => void zurueckholen("auftritte", eintrag.id)}
                  aria-label={`Wiederherstellen · ${eintrag.title}`}
                >
                  Wiederherstellen
                </button>
                {entferneKnopf("auftritte", eintrag.id, eintrag.title)}
              </li>
            ))}
          </ul>
        </section>
      )}
      {inhalt.dateien.length > 0 && (
        <section aria-labelledby="papierkorb-dateien-titel">
          <h2 id="papierkorb-dateien-titel">Dateien</h2>
          <ul className="papierkorb-liste" aria-label="Gelöschte Dateien">
            {inhalt.dateien.map((eintrag) => (
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
                <button
                  type="button"
                  className="knopf-leise"
                  disabled={beschaftigt !== null}
                  onClick={() => void zurueckholen("dateien", eintrag.id)}
                  aria-label={`Wiederherstellen · ${eintrag.fileName ?? "Datei"}`}
                >
                  Wiederherstellen
                </button>
                {entferneKnopf(
                  "dateien",
                  eintrag.id,
                  eintrag.fileName ?? "Datei",
                )}
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}
