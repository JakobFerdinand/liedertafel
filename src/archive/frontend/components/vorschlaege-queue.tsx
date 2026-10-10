"use client";

import { useCallback, useEffect, useState } from "react";
import { fetchMe, type MeResponse } from "@/lib/auth";
import {
  ablehnenVorschlag,
  annehmenVorschlag,
  auffrischenVorschlag,
  fetchVorschlaege,
  leseKonflikt,
  type Vorschlag,
} from "@/lib/provenanz";

// ARC-013-1: die "Vorschläge"-Verwaltung. Eine gewöhnliche Liste über den
// gespeicherten Bestand: sie funktioniert, während die KI pausiert.

type lokaleZeile = Vorschlag & { meldung?: string; entscheidung?: string };

/** Deutsche Namen der Eintragstypen für die Zielleiste. */
const eintragsNamen: Record<string, string> = {
  song: "Lied",
  arrangement: "Fassung (Arrangement)",
  musical_version: "Musikalische Fassung",
  asset: "Material",
  event: "Auftritt",
};

const feldNamen: Record<string, string> = {
  title: "Titel",
  composer: "Komponist",
  lyricist: "Textdichter",
  lyrics: "Liedtext",
  language: "Sprache",
  occasion: "Anlass",
  alternate_titles: "Andere Titel",
  tags: "Schlagwörter",
  label: "Bezeichnung",
  arranger: "Arrangeur",
  voice_configuration: "Stimmverteilung",
  accompaniment: "Begleitung",
  creator: "Ersteller",
  musical_key: "Tonart",
  description: "Beschreibung",
  voice_label: "Stimme",
  notes: "Notizen",
  source_note: "Quellenvermerk",
};

function feldName(feld: string): string {
  return feldNamen[feld] ?? feld;
}

/** Lesbarer Wert eines Vorschlags aus dem Nutzlast. */
function lesbarerWert(vorschlag: Vorschlag): string {
  const nutzlast = vorschlag.payload as {
    field?: string;
    value?: string | null;
  } | null;
  if (vorschlag.kind === "FieldSuggestion" && nutzlast)
    return nutzlast.value ? nutzlast.value : "(leer)";
  if (vorschlag.kind === "SongCreation") {
    const song = vorschlag.payload as {
      title?: string;
      composer?: string;
      lyricist?: string;
    } | null;
    return song?.title ? `„${song.title}“` : "(ohne Titel)";
  }
  if (vorschlag.kind === "SongPublication") return "veröffentlichen";
  return "(Vorschlag)";
}

export function VorschlaegeVerwaltung() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [zeilen, setZeilen] = useState<lokaleZeile[]>([]);
  const [geladen, setGeladen] = useState(false);
  const [fehler, setFehler] = useState("");
  const [status, setStatus] = useState("");
  const [busy, setBusy] = useState("");

  const laden = useCallback(async (signal?: AbortSignal) => {
    const antwort = await fetchMe(signal);
    setMe(antwort);
    if (!antwort.authenticated) return;
    const editor =
      antwort.roles.includes("Editor") ||
      antwort.roles.includes("Administrator");
    if (!editor) return;
    try {
      const liste = await fetchVorschlaege(signal);
      setZeilen(liste.proposals ?? []);
      setFehler("");
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 403) {
        setFehler("Keine Berechtigung für die Verwaltung.");
        return;
      }
      if (ursache instanceof Response) {
        setFehler("Die Vorschläge ließen sich nicht laden.");
        return;
      }
      throw ursache;
    } finally {
      setGeladen(true);
    }
  }, []);

  useEffect(() => {
    const abbruch = new AbortController();
    laden(abbruch.signal).catch(() =>
      setFehler("Die Vorschläge ließen sich nicht laden."),
    );
    return () => abbruch.abort();
  }, [laden]);

  const editor =
    me?.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"));

  async function entscheiden(
    zeile: lokaleZeile,
    pfad: "annehmen" | "ablehnen",
  ) {
    setBusy(zeile.id);
    setStatus("");
    zeile.meldung = undefined;
    try {
      const antwort =
        pfad === "annehmen"
          ? await annehmenVorschlag(zeile.id)
          : await ablehnenVorschlag(zeile.id);
      if (antwort.ok) {
        setStatus(
          pfad === "annehmen"
            ? "Vorschlag angenommen und angewendet."
            : "Vorschlag abgelehnt.",
        );
        await laden();
        return;
      }
      const konflikt = await leseKonflikt(antwort);
      const inhalt = (await antwort
        .clone()
        .json()
        .catch(() => null)) as { title?: string } | null;
      const titel =
        konflikt?.titel ??
        inhalt?.title ??
        "Das hat nicht geklappt. Bitte erneut versuchen.";
      setStatus(titel);
      if (konflikt) {
        setZeilen((bisher) =>
          bisher.map((eintrag) =>
            eintrag.id === zeile.id
              ? {
                  ...eintrag,
                  isStale: true,
                  meldung: undefined,
                }
              : eintrag,
          ),
        );
      }
      await laden();
    } catch {
      setStatus(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy("");
    }
  }

  async function auffrischen(zeile: lokaleZeile) {
    setBusy(zeile.id);
    setStatus("");
    try {
      const antwort = await auffrischenVorschlag(zeile.id);
      if (!antwort.ok) {
        const inhalt = (await antwort.json().catch(() => null)) as {
          title?: string;
        } | null;
        setStatus(inhalt?.title ?? "Die Aktualisierung hat nicht geklappt.");
        return;
      }
      setStatus(
        "Vorschlag auf den aktuellen Stand gebracht. Bitte noch einmal prüfen.",
      );
      await laden();
    } catch {
      setStatus(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy("");
    }
  }

  if (me?.authenticated && !editor) {
    return (
      <p className="feld-fehler" role="alert">
        Keine Berechtigung für die Verwaltung.
      </p>
    );
  }

  const gruppen = [...new Set(zeilen.map((zeile) => zeile.kindLabel))];

  return (
    <div className="vorschlaege-bereich">
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {geladen && !fehler && zeilen.length === 0 && (
        <p className="leerer-stand aria-leer">Keine offenen Vorschläge.</p>
      )}
      <output aria-live="polite" className="auth-erfolg">
        {status}
      </output>
      {gruppen.map((gruppennName) => (
        <section
          key={gruppennName}
          className="vorschlaege-gruppe"
          aria-labelledby={`vorschlaege-gruppe-${gruppennName.replace(/\s+/g, "-")}`}
        >
          <h2 id={`vorschlaege-gruppe-${gruppennName.replace(/\s+/g, "-")}`}>
            {gruppennName}
          </h2>
          {zeilen
            .filter((zeile) => zeile.kindLabel === gruppennName)
            .map((zeile) => (
              <article key={zeile.id} className="vorschlaege-eintrag">
                <p className="vorschlaege-ziel">
                  {eintragsNamen[zeile.targetEntityType ?? ""] ?? "Eintrag"}
                  {zeile.kind === "SongCreation" ? " (neu)" : ""}
                  {": "}
                  <strong>{lesbarerWert(zeile)}</strong>
                  {zeile.targetEntityType !== null &&
                    zeile.targetEntityType !== undefined &&
                    zeile.kind === "FieldSuggestion" &&
                    (zeile.payload as { field?: string } | null)?.field && (
                      <>
                        {" · "}
                        {feldName(
                          (zeile.payload as { field?: string }).field ?? "",
                        )}
                      </>
                    )}
                </p>
                <p className="vorschlaege-herkunft">
                  {zeile.source === "Ai" ? "KI" : "Automatische Auswertung"}
                  {zeile.confidence === "Sicher"
                    ? " · sicher gelesen"
                    : " · unsicher gelesen"}
                  {zeile.model ? ` · ${zeile.model}` : ""}
                  {zeile.sourceDescription
                    ? ` · ${zeile.sourceDescription}`
                    : ""}
                </p>
                <p className="vorschlaege-begruendung">{zeile.reason}</p>
                {zeile.isStale && (
                  <aside
                    aria-label="Vorschlagsziel wurde zwischenzeitlich geändert"
                    className="vorschlaege-abgelaufen"
                  >
                    <p>
                      <strong>
                        Ziel ist zwischenzeitlich geändert. Frischer Blick
                        nötig:
                      </strong>
                    </p>
                    <p>
                      Gespeicherter Wert:{" "}
                      <strong>
                        {zeile.targetCurrentSummary
                          ? zeile.targetCurrentSummary
                          : "(leer)"}
                      </strong>
                    </p>
                    <p>
                      Vorgeschlagen: <strong>{lesbarerWert(zeile)}</strong>
                    </p>
                  </aside>
                )}
                <div className="vorschlaege-aktionen">
                  <button
                    type="button"
                    disabled={busy === zeile.id}
                    onClick={() => entscheiden(zeile, "annehmen")}
                    aria-label={`Vorschlag für ${
                      feldName(
                        (zeile.payload as { field?: string } | null)?.field ??
                          "",
                      ) || lesbarerWert(zeile)
                    } annehmen`}
                  >
                    {busy === zeile.id ? "Wird bearbeitet …" : "Annehmen"}
                  </button>
                  <button
                    type="button"
                    disabled={busy === zeile.id}
                    onClick={() => entscheiden(zeile, "ablehnen")}
                    aria-label={`Vorschlag für ${
                      feldName(
                        (zeile.payload as { field?: string } | null)?.field ??
                          "",
                      ) || lesbarerWert(zeile)
                    } ablehnen`}
                  >
                    Ablehnen
                  </button>
                  {zeile.isStale && (
                    <button
                      type="button"
                      disabled={busy === zeile.id}
                      onClick={() => auffrischen(zeile)}
                      aria-label="Vorschlag auf den aktuellen Stand bringen"
                    >
                      Auf den aktuellen Stand bringen
                    </button>
                  )}
                </div>
              </article>
            ))}
        </section>
      ))}
    </div>
  );
}
