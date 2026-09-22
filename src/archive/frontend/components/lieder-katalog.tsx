"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import { fetchSongSearch, type Lied, type LiedSuchErgebnis } from "@/lib/songs";

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

export function LiederKatalog() {
  const suchParameter = useSearchParams();
  const router = useRouter();
  const suchWort = suchParameter.get("suche");
  const seiteWert = Number.parseInt(suchParameter.get("seite") ?? "1", 10);
  const seite = seiteWert > 1 ? seiteWert : 1;
  const abfrage = suchWort?.trim() ? suchWort.trim() : null;

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
        const neu = await fetchSongSearch(abfrage, seite, signal);
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
    [abfrage, seite],
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
      router.push(`/lieder/?suche=${encodeURIComponent(begriff)}&seite=1`);
      return;
    }
    router.push("/lieder/");
  }

  function seiteWechseln(naechste: number) {
    const parameter = new URLSearchParams();
    if (abfrage) parameter.set("suche", abfrage);
    parameter.set("seite", String(naechste));
    router.push(`/lieder/?${parameter.toString()}`);
  }

  if (fehler) {
    return (
      <div aria-live="polite">
        <p>{fehler}</p>
        <button type="button" onClick={() => setVersuch(versuch + 1)}>
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p>Mitgliedschaft wird geprüft …</p>
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
      <section
        className="lieder-suche auth-karte"
        aria-labelledby="lieder-suche-titel"
      >
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

      {editor && (
        <section
          className="auth-karte lied-anlegen"
          aria-labelledby="lied-anlegen-titel"
        >
          <h2 id="lied-anlegen-titel">Neues Lied</h2>
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
        </section>
      )}

      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}{" "}
          {hinweis.includes("erneute Anmeldung") && (
            <Link href="/anmelden/">Anmelden</Link>
          )}
        </output>
      )}

      <section aria-labelledby="lieder-titel" className="lieder-liste">
        <h2 id="lieder-titel">Liederkatalog</h2>
        {lieder === null ? (
          <p aria-live="polite">Lieder werden geladen …</p>
        ) : lieder.length === 0 ? (
          abfrage ? (
            <p>
              Keine Lieder gefunden. Bitte versuche andere Worte, etwa einen
              Kurztitel, einen Urheber oder die ersten Textzeilen.
            </p>
          ) : (
            <p>Noch keine Lieder im Katalog.</p>
          )
        ) : (
          <>
            <ul className="lieder-liste-einträge">
              {lieder.map((lied) => {
                const stellen = abfrage ? fundstellen(lied) : [];
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
                    {stellen.map((stelle) => (
                      <p key={stelle} className="lieder-fundstelle">
                        {stelle}
                      </p>
                    ))}
                    {editor && (
                      <p className="lieder-status">
                        {lied.published ? "Veröffentlicht" : "Entwurf"}
                      </p>
                    )}
                    {editor && (
                      <div className="lieder-aktionen">
                        <button
                          type="button"
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
                    {editor && bearbeitet === lied.id && (
                      <div className="lied-bearbeiten auth-karte">
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
