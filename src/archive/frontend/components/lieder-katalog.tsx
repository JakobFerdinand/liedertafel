"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { LiedAblegen } from "@/components/lied-ablegen";
import { fetchMe, type MeResponse } from "@/lib/auth";
import {
  type DateiArt,
  dateiArten,
  dateiArtText,
  fetchLiedSuche,
  type LiedListe,
} from "@/lib/lieder";

function istEditor(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

// Dateiarten der Adresse: kommagetrennt, ohne leere oder unbekannte Werte.
function dateiArtenAusAdresse(wert: string | null): DateiArt[] {
  const bekannt = dateiArten.map(([art]) => art);
  return (wert ?? "")
    .split(",")
    .map((eintrag) => eintrag.trim())
    .filter((eintrag): eintrag is DateiArt =>
      bekannt.includes(eintrag as DateiArt),
    );
}

export function LiederKatalog() {
  const suchParameter = useSearchParams();
  const router = useRouter();
  const suchWort = suchParameter.get("suche");
  // "Angefangen" (#178) öffnet "Neues Lied" mit dem gespeicherten Entwurf.
  const entwurfId = suchParameter.get("entwurf");
  const seiteWert = Number.parseInt(suchParameter.get("seite") ?? "1", 10);
  const seite = seiteWert > 1 ? seiteWert : 1;
  const abfrage = suchWort?.trim() ? suchWort.trim() : null;
  // Der Filterzustand lebt in der Adresse: besetzung, anlass und dateityp
  // (kommagetrennt).
  const besetzung = (suchParameter.get("besetzung") ?? "").trim();
  const anlass = (suchParameter.get("anlass") ?? "").trim();
  const dateiTypParameter = suchParameter.get("dateityp") ?? "";
  const dateiTypen = dateiArtenAusAdresse(dateiTypParameter);
  const hatFilter =
    besetzung.length > 0 || anlass.length > 0 || dateiTypen.length > 0;
  const aktiveFilter = [
    ...(besetzung
      ? [{ name: "besetzung", beschriftung: "Besetzung", wert: besetzung }]
      : []),
    ...(anlass
      ? [{ name: "anlass", beschriftung: "Anlass", wert: anlass }]
      : []),
    ...dateiTypen.map((art) => ({
      name: "dateityp",
      beschriftung: "Dateityp",
      wert: dateiArtText(art),
      art,
    })),
  ];

  const [me, setMe] = useState<MeResponse | null>(null);
  const [ergebnis, setErgebnis] = useState<LiedListe | null>(null);
  // Die Auswahllisten der Filter bleiben auch bei leerem Ergebnis erhalten.
  const [optionen, setOptionen] = useState<LiedListe["options"]>({
    voiceConfigurations: [],
    occasions: [],
  });
  const [fehler, setFehler] = useState("");
  const [versuch, setVersuch] = useState(0);
  const filterRef = useRef<HTMLDetailsElement>(null);

  const editor = me?.authenticated && istEditor(me);

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      setFehler("");
      const antwort = await fetchMe(signal);
      if (!signal?.aborted) setMe(antwort);
      if (!antwort.authenticated) return;
      try {
        const neu = await fetchLiedSuche(abfrage, seite, signal, {
          besetzung,
          anlass,
          dateiTypen: dateiArtenAusAdresse(dateiTypParameter),
        });
        if (!signal?.aborted) {
          setErgebnis(neu);
          setOptionen(neu.options);
        }
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
    [abfrage, seite, besetzung, anlass, dateiTypParameter],
  );

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus.
    void versuch;
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden, versuch]);

  // Adresse des Katalogs: Suchbegriff plus Filter; Suchen und Filtern
  // starten auf der ersten Seite.
  function katalogPfad(
    begriff: string | null,
    filter: { besetzung: string; anlass: string; dateiTypen: DateiArt[] },
    naechsteSeite: number,
  ): string {
    const parameter = new URLSearchParams();
    if (begriff) parameter.set("suche", begriff);
    if (filter.besetzung) parameter.set("besetzung", filter.besetzung);
    if (filter.anlass) parameter.set("anlass", filter.anlass);
    if (filter.dateiTypen.length > 0)
      parameter.set("dateityp", filter.dateiTypen.join(","));
    if (naechsteSeite > 1) parameter.set("seite", String(naechsteSeite));
    const zeichenkette = parameter.toString();
    return zeichenkette ? `/lieder/?${zeichenkette}` : "/lieder/";
  }

  const aktuellerFilter = { besetzung, anlass, dateiTypen };

  function suchen(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const begriff =
      new FormData(event.currentTarget).get("suche")?.toString().trim() || null;
    router.push(katalogPfad(begriff, aktuellerFilter, 1));
  }

  function filterAnwenden(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const daten = new FormData(event.currentTarget);
    filterSchliessen();
    router.push(
      katalogPfad(
        abfrage,
        {
          besetzung: daten.get("besetzung")?.toString().trim() ?? "",
          anlass: daten.get("anlass")?.toString().trim() ?? "",
          dateiTypen: dateiArtenAusAdresse(daten.getAll("dateityp").join(",")),
        },
        1,
      ),
    );
  }

  function filterZuruecksetzen() {
    filterSchliessen();
    router.push(
      katalogPfad(abfrage, { besetzung: "", anlass: "", dateiTypen: [] }, 1),
    );
  }

  function filterEntfernen(name: string, art?: DateiArt) {
    filterSchliessen();
    router.push(
      katalogPfad(
        abfrage,
        {
          besetzung: name === "besetzung" ? "" : besetzung,
          anlass: name === "anlass" ? "" : anlass,
          dateiTypen: art ? dateiTypen.filter((w) => w !== art) : dateiTypen,
        },
        1,
      ),
    );
  }

  function filterSchliessen() {
    if (filterRef.current) {
      filterRef.current.open = false;
      filterRef.current.querySelector("summary")?.focus();
    }
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

  const lieder = ergebnis?.lieder ?? null;
  const gesamt = ergebnis?.total ?? 0;
  const seitenGroesse = ergebnis?.pageSize ?? 20;
  const seitenZahl = Math.max(1, Math.ceil(gesamt / seitenGroesse));
  // Ein gesetzter Wert bleibt wählbar, auch wenn er (noch) kein Lied trägt.
  const besetzungen = [
    ...new Set([
      ...optionen.voiceConfigurations,
      ...(besetzung ? [besetzung] : []),
    ]),
  ];
  const anlaesse = [
    ...new Set([...optionen.occasions, ...(anlass ? [anlass] : [])]),
  ];

  return (
    <div>
      <div className="lieder-werkzeuge">
        <section className="lieder-suche" aria-label="Katalogsuche">
          <form onSubmit={suchen}>
            <label className="visually-hidden" htmlFor="lieder-suche-begriff">
              Lieder suchen
            </label>
            <div className="lieder-suche-felder">
              <input
                key={suchWort ?? ""}
                id="lieder-suche-begriff"
                name="suche"
                type="search"
                maxLength={200}
                placeholder="Titel …"
                defaultValue={suchWort ?? ""}
              />
              <button type="submit">Suchen</button>
            </div>
          </form>
        </section>

        <details
          ref={filterRef}
          className="lieder-filter"
          aria-labelledby="lieder-filter-titel"
        >
          <summary>
            <h2 id="lieder-filter-titel">Filter</h2>
          </summary>
          <form onSubmit={filterAnwenden}>
            <div className="lieder-filter-felder">
              <div>
                <label htmlFor="lieder-filter-besetzung">Besetzung</label>
                <select
                  key={besetzung}
                  id="lieder-filter-besetzung"
                  name="besetzung"
                  defaultValue={besetzung}
                >
                  <option value="">Alle</option>
                  {besetzungen.map((wert) => (
                    <option key={wert} value={wert}>
                      {wert}
                    </option>
                  ))}
                </select>
              </div>
              <div>
                <label htmlFor="lieder-filter-anlass">Anlass</label>
                <select
                  key={anlass}
                  id="lieder-filter-anlass"
                  name="anlass"
                  defaultValue={anlass}
                >
                  <option value="">Alle</option>
                  {anlaesse.map((wert) => (
                    <option key={wert} value={wert}>
                      {wert}
                    </option>
                  ))}
                </select>
              </div>
            </div>
            <fieldset className="lieder-filter-material">
              <legend>Dateityp</legend>
              <p className="lieder-filter-hinweis">
                Lied hat alle gewählten Dateiarten.
              </p>
              <div className="lieder-filter-auswahl">
                {dateiArten.map(([art, beschriftung]) => (
                  <label key={art} htmlFor={`lieder-filter-dateityp-${art}`}>
                    <input
                      key={`${art}:${dateiTypen.includes(art)}`}
                      id={`lieder-filter-dateityp-${art}`}
                      name="dateityp"
                      type="checkbox"
                      value={art}
                      defaultChecked={dateiTypen.includes(art)}
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
            open={entwurfId ? true : undefined}
          >
            <summary>
              <h2 id="lied-anlegen-titel">Neues Lied</h2>
            </summary>
            <LiedAblegen entwurfId={entwurfId} />
          </details>
        )}
      </div>

      {hatFilter && (
        <nav className="lieder-filter-aktiv" aria-label="Aktive Filter">
          <ul>
            {aktiveFilter.map((filter) => (
              <li key={`${filter.name}:${filter.wert}`}>
                <button
                  type="button"
                  className="knopf-leise"
                  aria-label={`${filter.beschriftung}: ${filter.wert} entfernen`}
                  onClick={() =>
                    filterEntfernen(
                      filter.name,
                      "art" in filter ? filter.art : undefined,
                    )
                  }
                >
                  {filter.beschriftung}: {filter.wert}{" "}
                  <span aria-hidden="true">×</span>
                </button>
              </li>
            ))}
          </ul>
          <button
            type="button"
            className="knopf-leise"
            onClick={filterZuruecksetzen}
          >
            Alle Filter zurücksetzen
          </button>
        </nav>
      )}

      <section
        aria-labelledby="lieder-ergebnisse-titel"
        className="lieder-liste"
      >
        <h2 id="lieder-ergebnisse-titel">
          {lieder !== null && (abfrage || hatFilter)
            ? gesamt === 1
              ? "1 Treffer"
              : `${gesamt} Treffer`
            : "Alle Lieder"}
        </h2>
        {lieder === null ? (
          <p aria-live="polite" className="auth-statuszeile">
            Lieder werden geladen …
          </p>
        ) : lieder.length === 0 ? (
          abfrage || hatFilter ? (
            <>
              <p>Keine Lieder gefunden. Bitte versuche einen anderen Titel.</p>
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
                const angaben = [
                  lied.voiceConfiguration,
                  lied.occasion,
                  ...lied.dateiTypen.map(dateiArtText),
                ].filter(Boolean);
                return (
                  <li key={lied.id} className="lieder-eintrag">
                    <div className="lieder-titelgruppe">
                      <h3>
                        <Link href={`/lied/?id=${encodeURIComponent(lied.id)}`}>
                          {lied.title}
                        </Link>
                      </h3>
                    </div>
                    <p className="lieder-urheber">
                      {lied.composer && <span>Komponist: {lied.composer}</span>}
                      {lied.lyricist && <span>Text: {lied.lyricist}</span>}
                      {!lied.composer &&
                        !lied.lyricist &&
                        "Urheber nicht erfasst"}
                    </p>
                    {angaben.length > 0 && (
                      <p className="lieder-fundstelle">{angaben.join(" · ")}</p>
                    )}
                    {/* Die Suche nennt, wo der Treffer stand (#142). */}
                    {abfrage && lied.matchIn && lied.matchIn.length > 0 && (
                      <p className="lieder-trefferfund">
                        <span>Gefunden: {lied.matchIn.join(" · ")}</span>
                      </p>
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
                  onClick={() =>
                    router.push(
                      katalogPfad(abfrage, aktuellerFilter, seite - 1),
                    )
                  }
                >
                  Zurück
                </button>
                <span className="lieder-seiten-stand">
                  Seite {seite} von {seitenZahl}
                </span>
                <button
                  type="button"
                  disabled={seite >= seitenZahl}
                  onClick={() =>
                    router.push(
                      katalogPfad(abfrage, aktuellerFilter, seite + 1),
                    )
                  }
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
