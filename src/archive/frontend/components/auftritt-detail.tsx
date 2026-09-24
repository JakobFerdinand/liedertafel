"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { AuftrittDokumente } from "@/components/auftritt-dokumente";
import { AuftrittFormular } from "@/components/auftritt-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import {
  type AuftrittDetails,
  auftrittArtName,
  fetchEvent,
} from "@/lib/events";

export function AuftrittDetail() {
  const suchParameter = useSearchParams();
  const id = suchParameter.get("id");
  const [me, setMe] = useState<MeResponse | null>(null);
  const [auftritt, setAuftritt] = useState<AuftrittDetails | null>(null);
  const [nichtGefunden, setNichtGefunden] = useState(false);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [bearbeitet, setBearbeitet] = useState(false);
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
        const details = await fetchEvent(id, signal);
        if (!signal?.aborted) setAuftritt(details);
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

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund
    // wird nicht nachgeladen.
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
      if (payload?.event) setAuftritt(payload.event);
      setErfolg(meldung);
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setAktionBusy("");
    }
  }

  // Nach Material-Änderungen kommt der Auftritt frisch; dieselben Zustände
  // wie beim ersten Laden (nicht gefunden / abgemeldet / Störung).
  function aktualisieren() {
    const abbruch = new AbortController();
    void laden(abbruch.signal).catch((ursache) => {
      if (abbruch.signal.aborted) return;
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
  }

  if (fehler) {
    return (
      <div aria-live="polite">
        <p className="hinweis-block">{fehler}</p>
        <p>
          <button type="button" onClick={() => setVersuch(versuch + 1)}>
            Erneut versuchen
          </button>
        </p>
      </div>
    );
  }
  if (me === null) {
    return (
      <div aria-live="polite">
        <p className="auth-statuszeile">Auftritt wird geladen …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um den Auftritt zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }
  if (nichtGefunden) {
    return (
      <div aria-live="polite">
        <p className="hinweis-block">
          Der Auftritt wurde nicht gefunden. Er ist entweder nicht
          veröffentlicht oder die Adresse ist nicht mehr gültig.
        </p>
        <p>
          <Link href="/auftritte/">Zum Auftrittsverzeichnis</Link>
        </p>
      </div>
    );
  }
  if (auftritt === null) {
    return (
      <div aria-live="polite">
        <p className="auth-statuszeile">Auftritt wird geladen …</p>
      </div>
    );
  }

  // Ehrliche Unsicherheit: nur das genaue Tag-Monat-Jahr-Datum gilt als
  // sicher; ungefähre Angaben und Teilangaben bleiben gekennzeichnet.
  const unsicher = auftritt.dateApproximate || auftritt.datePrecision !== "day";

  return (
    <div className="auftritt-detail">
      <article className="auftritt-ansicht">
        {editor && (
          <p className="lieder-status">
            {auftritt.published ? "Veröffentlicht" : "Entwurf"}
          </p>
        )}
        <h2>{auftritt.title}</h2>
        <p className="auftritte-art">{auftrittArtName(auftritt.kind)}</p>
        <p className="auftritt-datum">
          {auftritt.dateDisplay}
          {unsicher && <span className="datum-unsicher">Datum unsicher</span>}
        </p>
        {auftritt.venue && <p className="auftritt-ort">{auftritt.venue}</p>}
        {auftritt.startTime && (
          <p className="auftritt-zeit">{auftritt.startTime} Uhr</p>
        )}
      </article>

      {auftritt.notes && (
        <section
          className="auftritt-abschnitt"
          aria-labelledby="auftritt-hinweise-titel"
        >
          <h3 id="auftritt-hinweise-titel">Hinweise</h3>
          <p className="auftritt-text">{auftritt.notes}</p>
        </section>
      )}
      {auftritt.sourceNote && (
        <section
          className="auftritt-abschnitt"
          aria-labelledby="auftritt-quelle-titel"
        >
          <h3 id="auftritt-quelle-titel">Quelle</h3>
          <p className="auftritt-text">{auftritt.sourceNote}</p>
        </section>
      )}

      {/* Nützliche leere Abschnitte: Programm und Aufnahmen
          folgen in eigenen Abschnitten (ARC-026/032). */}
      <section
        className="auftritt-abschnitt"
        aria-labelledby="auftritt-programm-titel"
      >
        <h3 id="auftritt-programm-titel">Programm</h3>
        <p className="auftritt-leer">Das Programm wurde noch nicht erfasst.</p>
      </section>
      <AuftrittDokumente
        auftritt={auftritt}
        isEditor={editor === true}
        aktualisieren={aktualisieren}
      />
      <section
        className="auftritt-abschnitt"
        aria-labelledby="auftritt-aufnahmen-titel"
      >
        <h3 id="auftritt-aufnahmen-titel">Aufnahmen</h3>
        <p className="auftritt-leer">
          Zu diesem Auftritt sind noch keine Aufnahmen hinterlegt.
        </p>
      </section>

      {editor && (
        <section
          className="auth-karte auftritt-bearbeiten"
          aria-labelledby="auftritt-bearbeiten-titel"
        >
          <h2 id="auftritt-bearbeiten-titel">Auftritt bearbeiten</h2>
          <div className="auftritte-aktionen">
            <button
              type="button"
              className="knopf-leise"
              aria-expanded={bearbeitet}
              aria-controls="auftritt-bearbeiten-formular"
              disabled={aktionBusy !== ""}
              onClick={() => setBearbeitet(!bearbeitet)}
            >
              {bearbeitet ? "Bearbeiten schließen" : "Bearbeiten"}
            </button>
            {auftritt.published ? (
              <button
                type="button"
                disabled={aktionBusy !== ""}
                onClick={() =>
                  veroeffentlichen(
                    `/api/events/${encodeURIComponent(auftritt.id)}/unpublish`,
                    "Auftritt zurückgezogen. Er ist für Mitglieder nicht mehr sichtbar.",
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
                    `/api/events/${encodeURIComponent(auftritt.id)}/publish`,
                    "Auftritt veröffentlicht. Mitglieder sehen ihn ab sofort.",
                  )
                }
              >
                {aktionBusy !== ""
                  ? "Wird veröffentlicht …"
                  : "Veröffentlichen"}
              </button>
            )}
          </div>
          <div id="auftritt-bearbeiten-formular" hidden={!bearbeitet}>
            {bearbeitet && (
              <AuftrittFormular
                auftritt={auftritt}
                absendenText="Änderungen speichern"
                onAbbrechen={() => setBearbeitet(false)}
                onSuccess={(gespeichert) => {
                  setAuftritt(gespeichert);
                  setErfolg("Änderungen gespeichert.");
                  setHinweis("");
                }}
              />
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
        <Link href="/auftritte/">Zum Auftrittsverzeichnis</Link>
      </p>
    </div>
  );
}
