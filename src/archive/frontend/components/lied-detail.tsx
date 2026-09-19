"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { FassungsFormular } from "@/components/fassungs-formular";
import { FassungsWahl } from "@/components/fassungs-wahl";
import { LiedFormular } from "@/components/lied-formular";
import { NotenBereich } from "@/components/noten-bereich";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import { fetchSong, type LiedDetails } from "@/lib/songs";

type FassungsAuswahl = {
  arrangementId: string;
  versionId: string;
};

function bestimmeFassung(
  lied: LiedDetails,
  fassungParameter: string | null,
  versionParameter: string | null,
): FassungsAuswahl | null {
  const arrangement =
    lied.arrangements.find((eintrag) => eintrag.id === fassungParameter) ??
    lied.arrangements[0];
  if (!arrangement) return null;
  const fassung =
    arrangement.musicalVersions.find(
      (eintrag) => eintrag.id === versionParameter,
    ) ?? arrangement.musicalVersions[0];
  return {
    arrangementId: arrangement.id,
    versionId: fassung ? fassung.id : "",
  };
}

// Noten-Assets hängen an jeder Fassung; Schreibaktionen liefern derzeit
// Fassungen ohne Assets zurück, deshalb bleiben die letzten bekannten
// Noten beim Ersetzen des Liedes erhalten (ARC-015).
function notenErhalten(vorher: LiedDetails | null, naechstes: LiedDetails) {
  if (!vorher) return naechstes;
  return {
    ...naechstes,
    arrangements: naechstes.arrangements.map((arrangement) => {
      const alt = vorher.arrangements.find(
        (eintrag) => eintrag.id === arrangement.id,
      );
      return {
        ...arrangement,
        musicalVersions: arrangement.musicalVersions.map((fassung) => {
          const fassungAlt = alt?.musicalVersions.find(
            (eintrag) => eintrag.id === fassung.id,
          );
          return (fassung.assets?.length ?? 0) === 0 && fassungAlt
            ? { ...fassung, assets: fassungAlt.assets ?? [] }
            : fassung;
        }),
      };
    }),
  };
}

export function LiedDetail() {
  const suchParameter = useSearchParams();
  const router = useRouter();
  const id = suchParameter.get("id");
  const fassungParameter = suchParameter.get("fassung");
  const versionParameter = suchParameter.get("version");
  const [me, setMe] = useState<MeResponse | null>(null);
  const [lied, setLied] = useState<LiedDetails | null>(null);
  const [nichtGefunden, setNichtGefunden] = useState(false);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [arrangementBearbeitet, setArrangementBearbeitet] = useState<
    string | null
  >(null);
  const [fassungHinzufuegen, setFassungHinzufuegen] = useState<string | null>(
    null,
  );
  const [fassungBearbeitet, setFassungBearbeitet] = useState<string | null>(
    null,
  );
  const [versuch, setVersuch] = useState(0);

  const editor =
    me?.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"));

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      const antwort = await fetchMe(signal);
      if (!signal?.aborted) setMe(antwort);
      if (!antwort.authenticated) return;
      if (!id) {
        if (!signal?.aborted) setNichtGefunden(true);
        return;
      }
      try {
        const details = await fetchSong(id, signal);
        if (!signal?.aborted) setLied(details);
      } catch (ursache) {
        if (ursache instanceof Response && ursache.status === 401) {
          if (!signal?.aborted) setMe({ authenticated: false });
          return;
        }
        throw ursache;
      }
    },
    [id],
  );

  const notenAktualisieren = useCallback(async () => {
    if (!id) return;
    try {
      const details = await fetchSong(id);
      setLied((vorher) => notenErhalten(vorher, details));
    } catch {
      // Eine fehlgeschlagene Aktualisierung darf die Ansicht nicht abbrechen.
    }
  }, [id]);

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

  function fassungWaehlen(arrangementId: string, versionId: string) {
    const parameter = new URLSearchParams(suchParameter);
    parameter.set("fassung", arrangementId);
    parameter.set("version", versionId);
    router.replace(`/lied/?${parameter.toString()}`, { scroll: false });
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

  const auswahl = bestimmeFassung(lied, fassungParameter, versionParameter);

  const ausgewaehlteFassung = lied.arrangements
    .find((eintrag) => eintrag.id === auswahl?.arrangementId)
    ?.musicalVersions.find((eintrag) => eintrag.id === auswahl?.versionId);

  function gesichertSpeichern(gespeichert: LiedDetails, meldung: string) {
    setLied((vorher) => notenErhalten(vorher, gespeichert));
    setErfolg(meldung);
    setHinweis("");
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
          auswahl && (
            <FassungsWahl
              lied={lied}
              arrangementId={auswahl.arrangementId}
              versionId={auswahl.versionId}
              onSelect={fassungWaehlen}
            />
          )
        )}
        {auswahl && ausgewaehlteFassung && (
          <NotenBereich
            fassungId={ausgewaehlteFassung.id}
            fassungLabel={ausgewaehlteFassung.label}
            stimmenKonfiguration={
              lied.arrangements.find(
                (eintrag) => eintrag.id === auswahl?.arrangementId,
              )?.voiceConfiguration ?? null
            }
            assets={ausgewaehlteFassung.assets ?? []}
            isEditor={editor === true}
            aktualisieren={() => void notenAktualisieren()}
          />
        )}
      </article>

      {editor && (
        <section
          className="auth-karte lied-fassungen"
          aria-labelledby="lied-fassungen-titel"
        >
          <h2 id="lied-fassungen-titel">Fassungen pflegen</h2>
          {lied.arrangements.map((arrangement) => (
            <div key={arrangement.id} className="lied-fassung-block">
              <p className="lied-fassung-titel">{arrangement.label}</p>
              <div className="lieder-aktionen">
                <button
                  type="button"
                  onClick={() => {
                    setArrangementBearbeitet(
                      arrangementBearbeitet === arrangement.id
                        ? null
                        : arrangement.id,
                    );
                    setFassungHinzufuegen(null);
                    setFassungBearbeitet(null);
                  }}
                >
                  {arrangementBearbeitet === arrangement.id
                    ? "Bearbeiten schließen"
                    : "Arrangement bearbeiten"}
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setFassungHinzufuegen(
                      fassungHinzufuegen === arrangement.id
                        ? null
                        : arrangement.id,
                    );
                    setArrangementBearbeitet(null);
                    setFassungBearbeitet(null);
                  }}
                >
                  {fassungHinzufuegen === arrangement.id
                    ? "Hinzufügen schließen"
                    : "Fassung hinzufügen"}
                </button>
              </div>
              {arrangementBearbeitet === arrangement.id && (
                <FassungsFormular
                  variante="arrangement"
                  methode="patch"
                  pfad={`/api/arrangements/${encodeURIComponent(arrangement.id)}`}
                  idPraefix={`arrangement-${arrangement.id}`}
                  absendenText="Arrangement speichern"
                  anfang={{
                    label: arrangement.label,
                    person: arrangement.arranger ?? "",
                    zusatz: arrangement.voiceConfiguration ?? "",
                  }}
                  onSuccess={(gespeichert, meldung) => {
                    gesichertSpeichern(gespeichert, meldung);
                    setArrangementBearbeitet(null);
                  }}
                />
              )}
              {fassungHinzufuegen === arrangement.id && (
                <FassungsFormular
                  variante="version"
                  methode="post"
                  pfad={`/api/arrangements/${encodeURIComponent(arrangement.id)}/versions`}
                  idPraefix={`fassung-${arrangement.id}`}
                  absendenText="Fassung anlegen"
                  onSuccess={(gespeichert, meldung) => {
                    gesichertSpeichern(gespeichert, meldung);
                    setFassungHinzufuegen(null);
                  }}
                />
              )}
              {arrangement.musicalVersions.map((fassung) => (
                <div key={fassung.id} className="lied-fassung-version">
                  <p className="lied-fassung-titel">{fassung.label}</p>
                  <div className="lieder-aktionen">
                    <button
                      type="button"
                      onClick={() => {
                        setFassungBearbeitet(
                          fassungBearbeitet === fassung.id ? null : fassung.id,
                        );
                        setArrangementBearbeitet(null);
                        setFassungHinzufuegen(null);
                      }}
                    >
                      {fassungBearbeitet === fassung.id
                        ? "Bearbeiten schließen"
                        : "Fassung bearbeiten"}
                    </button>
                  </div>
                  {fassungBearbeitet === fassung.id && (
                    <FassungsFormular
                      variante="version"
                      methode="patch"
                      pfad={`/api/musical-versions/${encodeURIComponent(fassung.id)}`}
                      idPraefix={`fassung-bearbeiten-${fassung.id}`}
                      absendenText="Fassung speichern"
                      anfang={{
                        label: fassung.label,
                        person: fassung.creator ?? "",
                        zusatz: fassung.musicalKey ?? "",
                      }}
                      onSuccess={(gespeichert, meldung) => {
                        gesichertSpeichern(gespeichert, meldung);
                        setFassungBearbeitet(null);
                      }}
                    />
                  )}
                </div>
              ))}
            </div>
          ))}
          <h3>Neues Arrangement</h3>
          <FassungsFormular
            variante="arrangement"
            methode="post"
            pfad={`/api/songs/${encodeURIComponent(lied.id)}/arrangements`}
            idPraefix="neues-arrangement"
            absendenText="Arrangement anlegen"
            onSuccess={gesichertSpeichern}
          />
        </section>
      )}

      {editor && (
        <section className="auth-karte" aria-labelledby="lied-bearbeiten-titel">
          <h2 id="lied-bearbeiten-titel">Lied bearbeiten</h2>
          <LiedFormular
            lied={lied}
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
