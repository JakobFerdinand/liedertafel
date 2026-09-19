"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { LiedFormular } from "@/components/lied-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import { fetchSong, type LiedDetails } from "@/lib/songs";

export function LiedDetail() {
  const suchParameter = useSearchParams();
  const id = suchParameter.get("id");
  const [me, setMe] = useState<MeResponse | null>(null);
  const [lied, setLied] = useState<LiedDetails | null>(null);
  const [nichtGefunden, setNichtGefunden] = useState(false);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [versuch, setVersuch] = useState(0);

  const editor =
    me?.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"));

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      const antwort = await fetchMe(signal);
      if (!signal?.aborted) setMe(antwort);
      if (!id) {
        if (!signal?.aborted) setNichtGefunden(true);
        return;
      }
      const details = await fetchSong(id, signal);
      if (!signal?.aborted) setLied(details);
    },
    [id],
  );

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund wird
    // nicht nachgeladen.
    void versuch;
    setNichtGefunden(false);
    laden(abort.signal).catch((ursache) => {
      if (abort.signal.aborted) return;
      if (ursache instanceof Response) {
        if (ursache.status === 404) {
          setNichtGefunden(true);
          return;
        }
        if (ursache.status === 401) {
          setMe({ authenticated: false });
          return;
        }
      }
      setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden, versuch]);

  async function veroeffentlichen(pfad: string, meldung: string) {
    setHinweis("");
    setErfolg("");
    setAktionBusy(pfad);
    try {
      const response = await postAuth(pfad, {});
      const payload = await response.json().catch(() => null);
      if (!response.ok) {
        setHinweis(
          payload?.title ?? "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      if (payload?.song) setLied(payload.song);
      setErfolg(meldung);
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
        <button type="button" onClick={() => setVersuch(versuch + 1)}>
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p>Lied wird geladen …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um das Lied zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }
  if (nichtGefunden) {
    return (
      <div aria-live="polite">
        <p>
          Dieses Lied wurde nicht gefunden. Es ist entweder nicht veröffentlicht
          oder die Adresse ist nicht mehr gültig.
        </p>
        <Link href="/lieder/">Zum Liederkatalog</Link>
      </div>
    );
  }
  if (lied === null) {
    return (
      <div aria-live="polite">
        <p>Lied wird geladen …</p>
      </div>
    );
  }

  return (
    <div>
      <article className="lied-ansicht">
        {editor && (
          <p className="lieder-status lied-status-kopf">
            {lied.published ? "Veröffentlicht" : "Entwurf"}
          </p>
        )}
        <h2>{lied.title}</h2>
        {(lied.composer || lied.lyricist) && (
          <p className="lieder-urheber">
            {[
              lied.composer ? `Komponist: ${lied.composer}` : null,
              lied.lyricist ? `Text: ${lied.lyricist}` : null,
            ]
              .filter(Boolean)
              .join(" · ")}
          </p>
        )}
        {lied.arrangements.length === 0 ? (
          <p>Für dieses Lied ist noch keine Fassung erfasst.</p>
        ) : (
          <ul className="lied-arrangements">
            {lied.arrangements.map((arrangement) => (
              <li key={arrangement.id}>
                <h3>{arrangement.label}</h3>
                {arrangement.arranger && (
                  <p className="feld-hinweis">
                    Bearbeitung: {arrangement.arranger}
                  </p>
                )}
                <ul>
                  {arrangement.musicalVersions.map((fassung) => (
                    <li key={fassung.id}>
                      {fassung.label}
                      {fassung.creator ? ` · ${fassung.creator}` : ""}
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>
        )}
      </article>

      {editor && (
        <section className="auth-karte" aria-labelledby="lied-bearbeiten-titel">
          <h2 id="lied-bearbeiten-titel">Lied bearbeiten</h2>
          <LiedFormular
            lied={lied}
            beschriftung="Lied bearbeiten"
            absendenText="Änderungen speichern"
            onSuccess={(gespeichert) => {
              setLied(gespeichert as LiedDetails);
              setErfolg("Änderungen gespeichert.");
              setHinweis("");
            }}
          />
          <div className="auth-aktionen">
            {lied.published ? (
              <button
                type="button"
                disabled={aktionBusy !== ""}
                onClick={() =>
                  veroeffentlichen(
                    `/api/songs/${encodeURIComponent(lied.id)}/unpublish`,
                    "Lied zurückgezogen. Es ist für Mitglieder nicht mehr sichtbar.",
                  )
                }
              >
                {aktionBusy !== ""
                  ? "Wird zurückgezogen …"
                  : "Veröffentlichung zurückziehen"}
              </button>
            ) : (
              <button
                type="button"
                disabled={aktionBusy !== ""}
                onClick={() =>
                  veroeffentlichen(
                    `/api/songs/${encodeURIComponent(lied.id)}/publish`,
                    "Lied veröffentlicht. Mitglieder sehen es ab sofort.",
                  )
                }
              >
                {aktionBusy !== ""
                  ? "Wird veröffentlicht …"
                  : "Veröffentlichen"}
              </button>
            )}
          </div>
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
      <p>
        <Link href="/lieder/">Zum Liederkatalog</Link>
      </p>
    </div>
  );
}
