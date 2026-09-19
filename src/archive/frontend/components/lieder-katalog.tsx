"use client";

import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import { fetchSongs, type Lied } from "@/lib/songs";

function istEditor(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

export function LiederKatalog() {
  const [me, setMe] = useState<MeResponse | null>(null);
  const [lieder, setLieder] = useState<Lied[] | null>(null);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [bearbeitet, setBearbeitet] = useState<string | null>(null);

  const editor = me?.authenticated && istEditor(me);

  const laden = useCallback(async (signal?: AbortSignal) => {
    const antwort = await fetchMe(signal);
    if (!signal?.aborted) setMe(antwort);
    if (!antwort.authenticated) return;
    try {
      const liste = await fetchSongs(signal);
      if (!signal?.aborted) setLieder(liste);
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
  }, []);

  useEffect(() => {
    const abort = new AbortController();
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden]);

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
        setLieder((bisher) =>
          (bisher ?? []).map((eintrag) =>
            eintrag.id === payload.song.id ? payload.song : eintrag,
          ),
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

  if (fehler) {
    return (
      <div aria-live="polite">
        <p>{fehler}</p>
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

  return (
    <div>
      {editor && (
        <section
          className="auth-karte lied-anlegen"
          aria-labelledby="lied-anlegen-titel"
        >
          <h2 id="lied-anlegen-titel">Neues Lied</h2>
          <LiedFormular
            absendenText="Lied anlegen"
            onSuccess={(gespeichert) => {
              setLieder((bisher) => [...(bisher ?? []), gespeichert]);
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
          <p>Noch keine Lieder im Katalog.</p>
        ) : (
          <ul className="lieder-liste-einträge">
            {lieder.map((lied) => (
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
                <p className="lieder-urheber">
                  {lied.composer || lied.lyricist
                    ? [
                        lied.composer ? `Komponist: ${lied.composer}` : null,
                        lied.lyricist ? `Text: ${lied.lyricist}` : null,
                      ]
                        .filter(Boolean)
                        .join(" · ")
                    : null}
                </p>
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
                        setBearbeitet(bearbeitet === lied.id ? null : lied.id)
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
                        setLieder((bisher) =>
                          (bisher ?? []).map((eintrag) =>
                            eintrag.id === gespeichert.id
                              ? gespeichert
                              : eintrag,
                          ),
                        );
                        setErfolg("Änderungen gespeichert.");
                        setHinweis("");
                        setBearbeitet(null);
                      }}
                    />
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
