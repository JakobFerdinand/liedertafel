"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import {
  fetchSongSearch,
  type Lied,
  type LiedSuchErgebnis,
  materialAdressWerte,
} from "@/lib/songs";

function istEditor(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

// „matchedIn" wird zu deutschen Fundstellen; ein Arrangement-Treffer nennt
// zusätzlich die erste getroffene Fassung (ARC-020). Der Rückgriff auf leere
// Felder hält alte Bestände ohne Suchdaten darstellbar.
function fundstellen(lied: Lied): string[] {
  const felder = lied.matchedIn ?? [];
  const stellen: string[] = [];
  const fassung = lied.arrangements?.[0];
  if (felder.includes("arrangements") && fassung) {
    stellen.push(`Getroffen: Fassung „${fassung.label}“`);
  }
  if (felder.includes("lyrics")) stellen.push("Getroffen im Liedtext.");
  return stellen;
}

// Freitextfilter der Katalogadresse (ARC-023) mit ihren Formularnamen; die
// Materialwerte werden beim Abholen in die Materialarten übersetzt (ARC-032
// ergänzt die Aufnahmen dort).
const filterTextfelder = [
  ["stimmbesetzung", "Stimmverteilung"],
  ["begleitung", "Begleitung"],
  ["tonart", "Tonart"],
  ["sprache", "Sprache"],
  ["anlass", "Anlass"],
  ["tag", "Schlagwort"],
] as const;

// Materialwerte der Adresse: kommagetrennt, beschnitten, ohne leere oder
// unbekannte Einträge (ein eigenhändig verbogener Wert filtert nicht).
function materialAusAdresse(wert: string | null): string[] {
  const bekannte = new Set(materialAdressWerte);
  return (wert ?? "")
    .split(",")
    .map((eintrag) => eintrag.trim())
    .filter((eintrag) => bekannte.has(eintrag));
}

export function LiederKatalog() {
  const suchParameter = useSearchParams();
  const router = useRouter();
  const suchWort = suchParameter.get("suche");
  const seiteWert = Number.parseInt(suchParameter.get("seite") ?? "1", 10);
  const seite = seiteWert > 1 ? seiteWert : 1;
  const abfrage = suchWort?.trim() ? suchWort.trim() : null;
  // Filterzustand lebt in der Adresse und wird dort beschnitten gelesen;
  // die Materialwerte stehen kommagetrennt (noten,audio,midi).
  const stimmbesetzung = (suchParameter.get("stimmbesetzung") ?? "").trim();
  const begleitung = (suchParameter.get("begleitung") ?? "").trim();
  const tonart = (suchParameter.get("tonart") ?? "").trim();
  const sprache = (suchParameter.get("sprache") ?? "").trim();
  const anlass = (suchParameter.get("anlass") ?? "").trim();
  const schlagwort = (suchParameter.get("tag") ?? "").trim();
  const materialParameter = suchParameter.get("material") ?? "";
  const materialWerte = materialAusAdresse(materialParameter);
  // Anfangswerte der Filterfelder für die Aufklapper-Eingaben.
  const filterAnfang: Record<string, string> = {
    stimmbesetzung,
    begleitung,
    tonart,
    sprache,
    anlass,
    tag: schlagwort,
  };
  const hatFilter =
    [stimmbesetzung, begleitung, tonart, sprache, anlass, schlagwort].some(
      Boolean,
    ) || materialWerte.length > 0;
  // Fassungsfilter (Stimmverteilung, Begleitung, Tonart, Material) lassen
  // die Treffer die passende Fassung nennen; reine Liedfilter tun das nicht.
  const hatFassungsFilter =
    stimmbesetzung.length > 0 ||
    begleitung.length > 0 ||
    tonart.length > 0 ||
    materialWerte.length > 0;

  const [me, setMe] = useState<MeResponse | null>(null);
  const [ergebnis, setErgebnis] = useState<LiedSuchErgebnis | null>(null);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [bearbeitet, setBearbeitet] = useState<string | null>(null);
  const [versuch, setVersuch] = useState(0);

  const editor = me?.authenticated && istEditor(me);

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      setFehler("");
      const antwort = await fetchMe(signal);
      if (!signal?.aborted) setMe(antwort);
      if (!antwort.authenticated) return;
      try {
        const neu = await fetchSongSearch(abfrage, seite, signal, {
          stimmbesetzung,
          begleitung,
          tonart,
          sprache,
          anlass,
          tag: schlagwort,
          materialien: materialAusAdresse(materialParameter),
        });
        if (!signal?.aborted) setErgebnis(neu);
      } catch (ursache) {
        if (
          ursache instanceof Response &&
          ursache.status === 401 &&
          !signal?.aborted
        ) {
          setMe({ authenticated: false });
          return;
        }
        throw ursache;
      }
    },
    // Nur die beschnittenen URL-Werte fließen hier ein; so bleibt der
    // Abruf stabil, solange die Adresse unverändert bleibt.
    [
      abfrage,
      seite,
      stimmbesetzung,
      begleitung,
      tonart,
      sprache,
      anlass,
      schlagwort,
      materialParameter,
    ],
  );

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund wird
    // nicht nachgeladen.
    void versuch;
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden, versuch]);

  async function liedAktion(
    schluessel: string,
    pfad: string,
    erfolgsMeldung: string,
  ) {
    setHinweis("");
    setErfolg("");
    setAktionBusy(schluessel);
    try {
      const response = await postAuth(pfad, {});
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      if (payload?.song) {
        setErgebnis((bisher) =>
          bisher
            ? {
                ...bisher,
                songs: bisher.songs.map((eintrag) =>
                  eintrag.id === payload.song.id ? payload.song : eintrag,
                ),
              }
            : bisher,
        );
      }
      setErfolg(erfolgsMeldung);
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setAktionBusy("");
    }
  }

  function suchen(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const begriff = new FormData(event.currentTarget)
      .get("suche")
      ?.toString()
      .trim();
    if (begriff) {
      // Eine neue Suche startet vorne, hält aber die gesetzten Filter fest.
      router.push(katalogPfad(begriff, filterParameter(suchParameter), 1));
      return;
    }
    router.push("/lieder/");
  }

  // Filterwerte aus der Adresse oder dem Filterformular: beschnitten,
  // leere bleiben weg, Material in Formularreihenfolge.
  function filterParameter(
    quelle: FormData | URLSearchParams,
  ): URLSearchParams {
    const parameter = new URLSearchParams();
    for (const [name] of filterTextfelder) {
      const wert = quelle.get(name)?.toString().trim() ?? "";
      if (wert) parameter.set(name, wert);
    }
    const materialien = quelle
      .getAll("material")
      .map((wert) => wert.toString().trim())
      .filter(Boolean);
    if (materialien.length > 0)
      parameter.set("material", materialien.join(","));
    return parameter;
  }

  // Katalogadresse: Suchbegriff plus Filterwerte, Seite gesetzt oder auf 1
  // zurückgesetzt (Suchen und Filtern starten vorne).
  function katalogPfad(
    begriff: string | null,
    filter: URLSearchParams,
    naechsteSeite: number,
  ): string {
    const parameter = new URLSearchParams();
    if (begriff) parameter.set("suche", begriff);
    for (const [name, wert] of filter) parameter.set(name, wert);
    parameter.set("seite", String(naechsteSeite));
    return `/lieder/?${parameter.toString()}`;
  }

  function filterAnwenden(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    router.push(
      katalogPfad(
        abfrage,
        filterParameter(new FormData(event.currentTarget)),
        1,
      ),
    );
  }

  // Zurücksetzen löscht die Filter, behält aber die laufende Suche; die
  // Seite fällt auf den Anfang zurück (ohne seite-Parameter).
  function filterZuruecksetzen() {
    const parameter = new URLSearchParams();
    if (abfrage) parameter.set("suche", abfrage);
    const zeichenkette = parameter.toString();
    router.push(zeichenkette ? `/lieder/?${zeichenkette}` : "/lieder/");
  }

  function seiteWechseln(naechste: number) {
    router.push(katalogPfad(abfrage, filterParameter(suchParameter), naechste));
  }

  if (fehler) {
    return (
      <div aria-live="polite">
        <p className="verbindungs-fehler">{fehler}</p>
        <button type="button" onClick={() => setVersuch(versuch + 1)}>
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p className="auth-statuszeile">Mitgliedschaft wird geprüft …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um den Liederkatalog zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }

  const lieder = ergebnis?.songs ?? null;
  const gesamt = ergebnis?.total ?? 0;
  const seitenGröße = ergebnis?.pageSize ?? 20;
  const seitenZahl = Math.max(1, Math.ceil(gesamt / seitenGröße));

  return (
    <div>
      <section className="lieder-suche" aria-labelledby="lieder-suche-titel">
        <h2 id="lieder-suche-titel">Suche im Katalog</h2>
        <form onSubmit={suchen}>
          <label htmlFor="lieder-suche-begriff">Lieder suchen</label>
          <div className="lieder-suche-felder">
            <input
              key={suchWort ?? ""}
              id="lieder-suche-begriff"
              name="suche"
              type="search"
              maxLength={200}
              placeholder="Titel, Urheber oder Textworte …"
              defaultValue={suchWort ?? ""}
            />
            <button type="submit">Suchen</button>
          </div>
        </form>
      </section>

      {/* Repertoirefilter (ARC-023): Aufklapper neben der Suchkomponisten-
          Karte, aufgeklappt, wenn Filterparameter in der Adresse stehen. */}
      <details
        className="lieder-filter"
        open={hatFilter}
        aria-labelledby="lieder-filter-titel"
      >
        <summary>
          <h2 id="lieder-filter-titel">Filter</h2>
          <span className="lieder-filter-umschalter" aria-hidden="true">
            <span className="lieder-filter-auf">Ausklappen</span>
            <span className="lieder-filter-zu">Einklappen</span>
          </span>
        </summary>
        <form onSubmit={filterAnwenden}>
          <div className="lieder-filter-felder">
            {filterTextfelder.map(([name, beschriftung]) => (
              <div key={name}>
                <label htmlFor={`lieder-filter-${name}`}>{beschriftung}</label>
                <input
                  key={filterAnfang[name]}
                  id={`lieder-filter-${name}`}
                  name={name}
                  type="text"
                  maxLength={200}
                  defaultValue={filterAnfang[name]}
                />
              </div>
            ))}
          </div>
          <fieldset className="lieder-filter-material">
            <legend>Material</legend>
            <div className="lieder-filter-auswahl">
              {/* Aufnahmen ergänzen hier ihre Materialwahl (ARC-032). */}
              {[
                ["noten", "Noten"],
                ["audio", "Audio"],
                ["midi", "MIDI"],
              ].map(([wert, beschriftung]) => (
                <label key={wert} htmlFor={`lieder-filter-material-${wert}`}>
                  <input
                    key={`${wert}:${materialWerte.includes(wert)}`}
                    id={`lieder-filter-material-${wert}`}
                    name="material"
                    type="checkbox"
                    value={wert}
                    defaultChecked={materialWerte.includes(wert)}
                  />
                  {beschriftung}
                </label>
              ))}
            </div>
          </fieldset>
          <div className="lieder-filter-aktionen">
            <button type="submit">Filtern</button>
            <button
              type="button"
              className="knopf-leise"
              onClick={filterZuruecksetzen}
            >
              Zurücksetzen
            </button>
          </div>
        </form>
      </details>

      {editor && (
        <details
          className="auth-karte lied-anlegen"
          aria-labelledby="lied-anlegen-titel"
        >
          <summary>
            <h2 id="lied-anlegen-titel">Neues Lied</h2>
            <span className="lied-anlegen-umschalter" aria-hidden="true">
              <span className="lied-anlegen-auf">Ausklappen</span>
              <span className="lied-anlegen-zu">Einklappen</span>
            </span>
          </summary>
          <LiedFormular
            absendenText="Lied anlegen"
            onSuccess={(gespeichert) => {
              setErgebnis((bisher) =>
                bisher
                  ? {
                      ...bisher,
                      total: bisher.total + 1,
                      songs: [...bisher.songs, gespeichert],
                    }
                  : bisher,
              );
              setErfolg("Lied angelegt.");
              setHinweis("");
            }}
          />
        </details>
      )}

      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="auth-fehler">
          {hinweis}{" "}
          {hinweis.includes("erneute Anmeldung") && (
            <Link href="/anmelden/">Anmelden</Link>
          )}
        </output>
      )}

      <section aria-labelledby="lieder-titel" className="lieder-liste">
        <h2 id="lieder-titel">Liederkatalog</h2>
        {lieder !== null && gesamt > 0 && (abfrage || hatFilter) && (
          <p className="lieder-anzahl">
            {gesamt === 1 ? "1 Lied gefunden." : `${gesamt} Lieder gefunden.`}
          </p>
        )}
        {lieder === null ? (
          <p aria-live="polite" className="auth-statuszeile">
            Lieder werden geladen …
          </p>
        ) : lieder.length === 0 ? (
          abfrage || hatFilter ? (
            <>
              <p>
                Keine Lieder gefunden. Bitte versuche andere Worte, etwa einen
                Kurztitel, einen Urheber oder die ersten Textzeilen.
              </p>
              {hatFilter && (
                <p className="lieder-filter-leer">
                  <button
                    type="button"
                    className="knopf-leise"
                    onClick={filterZuruecksetzen}
                  >
                    Filter zurücksetzen
                  </button>
                </p>
              )}
            </>
          ) : (
            <p>Noch keine Lieder im Katalog.</p>
          )
        ) : (
          <>
            <ul className="lieder-liste-einträge">
              {lieder.map((lied) => {
                const stellen = abfrage ? fundstellen(lied) : [];
                // Mit Fassungsfilter nennt jeder Treffer die Fassungen,
                // die die Fassungsbedingungen wirklich erfüllen (ARC-023).
                const passende = hatFassungsFilter
                  ? (lied.matchedArrangements ?? []).map(
                      (fassung) => `Passende Fassung: „${fassung.label}“`,
                    )
                  : [];
                const hinweise = [...new Set([...stellen, ...passende])];
                const andere = lied.alternateTitles ?? [];
                return (
                  <li key={lied.id} className="lieder-eintrag">
                    <h3>
                      {lied.published ? (
                        <Link href={`/lied/?id=${encodeURIComponent(lied.id)}`}>
                          {lied.title}
                        </Link>
                      ) : (
                        lied.title
                      )}
                    </h3>
                    {andere.length > 0 && (
                      <p className="lieder-andere-titel">
                        Auch bekannt als: {andere.join(", ")}
                      </p>
                    )}
                    <p className="lieder-urheber">
                      {lied.composer || lied.lyricist
                        ? [
                            lied.composer
                              ? `Komponist: ${lied.composer}`
                              : null,
                            lied.lyricist ? `Text: ${lied.lyricist}` : null,
                          ]
                            .filter(Boolean)
                            .join(" · ")
                        : null}
                    </p>
                    {hinweise.length > 0 && (
                      <div className="lieder-fundstellen">
                        {hinweise.map((stelle) => (
                          <p key={stelle} className="lieder-fundstelle">
                            {stelle}
                          </p>
                        ))}
                      </div>
                    )}
                    {editor && (
                      <p className="lieder-status">
                        {lied.published ? "Veröffentlicht" : "Entwurf"}
                      </p>
                    )}
                    {editor && (
                      <div className="lieder-aktionen">
                        <button
                          type="button"
                          className="knopf-leise"
                          aria-expanded={bearbeitet === lied.id}
                          aria-controls={`lied-bearbeiten-${lied.id}`}
                          disabled={aktionBusy !== ""}
                          onClick={() =>
                            setBearbeitet(
                              bearbeitet === lied.id ? null : lied.id,
                            )
                          }
                        >
                          {bearbeitet === lied.id
                            ? "Bearbeiten schließen"
                            : "Bearbeiten"}
                        </button>
                        {lied.published ? (
                          <button
                            type="button"
                            disabled={aktionBusy !== ""}
                            onClick={() =>
                              liedAktion(
                                `unpublish:${lied.id}`,
                                `/api/songs/${encodeURIComponent(lied.id)}/unpublish`,
                                "Lied zurückgezogen. Es ist für Mitglieder nicht mehr sichtbar.",
                              )
                            }
                          >
                            {aktionBusy === `unpublish:${lied.id}`
                              ? "Wird zurückgezogen …"
                              : "Zurückziehen"}
                          </button>
                        ) : (
                          <button
                            type="button"
                            disabled={aktionBusy !== ""}
                            onClick={() =>
                              liedAktion(
                                `publish:${lied.id}`,
                                `/api/songs/${encodeURIComponent(lied.id)}/publish`,
                                "Lied veröffentlicht. Mitglieder sehen es ab sofort.",
                              )
                            }
                          >
                            {aktionBusy === `publish:${lied.id}`
                              ? "Wird veröffentlicht …"
                              : "Veröffentlichen"}
                          </button>
                        )}
                      </div>
                    )}
                    {editor && (
                      <div
                        id={`lied-bearbeiten-${lied.id}`}
                        className="lied-bearbeiten auth-karte"
                        hidden={bearbeitet !== lied.id}
                      >
                        {bearbeitet === lied.id && (
                          <LiedFormular
                            lied={lied}
                            absendenText="Änderungen speichern"
                            onSuccess={(gespeichert) => {
                              setErgebnis((bisher) =>
                                bisher
                                  ? {
                                      ...bisher,
                                      songs: bisher.songs.map((eintrag) =>
                                        eintrag.id === gespeichert.id
                                          ? gespeichert
                                          : eintrag,
                                      ),
                                    }
                                  : bisher,
                              );
                              setErfolg("Änderungen gespeichert.");
                              setHinweis("");
                              setBearbeitet(null);
                            }}
                          />
                        )}
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
            {seitenZahl > 1 && (
              <nav className="lieder-seiten" aria-label="Katalogseiten">
                <button
                  type="button"
                  disabled={seite <= 1}
                  onClick={() => seiteWechseln(seite - 1)}
                >
                  Zurück
                </button>
                <span className="lieder-seiten-stand">
                  Seite {seite} von {seitenZahl}
                </span>
                <button
                  type="button"
                  disabled={seite >= seitenZahl}
                  onClick={() => seiteWechseln(seite + 1)}
                >
                  Weiter
                </button>
              </nav>
            )}
          </>
        )}
      </section>
    </div>
  );
}
