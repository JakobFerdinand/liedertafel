"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { AuftrittFormular } from "@/components/auftritt-formular";
import { fetchMe, type MeResponse, postAuth } from "@/lib/auth";
import {
  type Auftritt,
  type AuftrittDetails,
  type AuftrittSuchErgebnis,
  auftrittArtName,
  fetchEvents,
} from "@/lib/events";

function istEditor(me: MeResponse): boolean {
  return (
    me.authenticated &&
    (me.roles.includes("Editor") || me.roles.includes("Administrator"))
  );
}

// Die Jahreswahl der Adresse: „ohne" wählt die Auftritte ohne Datum,
// eine Zahl das Jahr; ohne Wert liest sich der ganze Bestand.
type JahresAuswahl = number | "ohne" | null;

function jahrAusAdresse(wert: string | null): JahresAuswahl {
  if (wert === "ohne") return "ohne";
  const zahl = Number.parseInt(wert ?? "", 10);
  return Number.isInteger(zahl) ? zahl : null;
}

export function AuftritteBereich() {
  const suchParameter = useSearchParams();
  const router = useRouter();
  const jahr = jahrAusAdresse(suchParameter.get("jahr"));

  const [me, setMe] = useState<MeResponse | null>(null);
  const [ergebnis, setErgebnis] = useState<AuftrittSuchErgebnis | null>(null);
  const [fehler, setFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [aktionBusy, setAktionBusy] = useState("");
  const [bearbeitet, setBearbeitet] = useState<string | null>(null);
  const [versuch, setVersuch] = useState(0);

  const editor = me?.authenticated && istEditor(me);

  function ersetzeAuftritt(gespeichert: AuftrittDetails) {
    setErgebnis((bisher) =>
      bisher
        ? {
            ...bisher,
            events: bisher.events.map((eintrag) =>
              eintrag.id === gespeichert.id
                ? (gespeichert as Auftritt)
                : eintrag,
            ),
          }
        : bisher,
    );
  }

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      setFehler("");
      const antwort = await fetchMe(signal);
      if (!signal?.aborted) setMe(antwort);
      if (!antwort.authenticated) return;
      try {
        if (jahr === null || jahr === "ohne") {
          // Der ganze Bestand kommt ungeteilt; die undatierten Auftritte
          // wählt die Ansicht selbst aus, weil der Vertrag für sie keinen
          // eigenen Abrufparameter kennt.
          const neu = await fetchEvents({}, signal);
          if (!signal?.aborted) {
            setErgebnis(
              jahr === "ohne"
                ? {
                    ...neu,
                    events: neu.events.filter(
                      (eintrag) => eintrag.dateYear === null,
                    ),
                  }
                : neu,
            );
          }
        } else {
          const neu = await fetchEvents({ year: jahr }, signal);
          if (!signal?.aborted) setErgebnis(neu);
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
    // Nur die Jahresauswahl fließt hier ein; so bleibt der Abruf stabil,
    // solange die Adresse unverändert bleibt.
    [jahr],
  );

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus; im Hintergrund
    // wird nicht nachgeladen.
    void versuch;
    laden(abort.signal).catch(() => {
      if (!abort.signal.aborted)
        setFehler("Das Archiv antwortet nicht. Bitte erneut versuchen.");
    });
    return () => abort.abort();
  }, [laden, versuch]);

  function jahrWaehlen(auswahl: JahresAuswahl) {
    const parameter = new URLSearchParams();
    if (auswahl === "ohne") parameter.set("jahr", "ohne");
    else if (typeof auswahl === "number")
      parameter.set("jahr", String(auswahl));
    const zeichenkette = parameter.toString();
    router.push(zeichenkette ? `/auftritte/?${zeichenkette}` : "/auftritte/");
  }

  async function auftrittAktion(
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
      if (payload?.event) {
        setErgebnis((bisher) =>
          bisher
            ? {
                ...bisher,
                events: bisher.events.map((eintrag) =>
                  eintrag.id === payload.event.id ? payload.event : eintrag,
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
        <p className="auth-statuszeile">Mitgliedschaft wird geprüft …</p>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <p>Bitte anmelden, um das Auftrittsverzeichnis zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }

  const auftritte = ergebnis?.events ?? null;
  const jahre = ergebnis?.years ?? [];
  const hatJahresFilter = jahr !== null;

  return (
    <div>
      {editor && (
        <details
          className="auth-karte auftritt-anlegen"
          open
          aria-labelledby="auftritt-anlegen-titel"
        >
          <summary>
            <h2 id="auftritt-anlegen-titel">Neuer Auftritt</h2>
            <span className="auftritt-anlegen-umschalter" aria-hidden="true">
              <span className="auftritt-anlegen-auf">Ausklappen</span>
              <span className="auftritt-anlegen-zu">Einklappen</span>
            </span>
          </summary>
          <AuftrittFormular
            absendenText="Auftritt anlegen"
            onSuccess={() => {
              // Der ganze Bestand kommt frisch: so bleiben Sortierung
              // und die Jahresleiste (years-Zusammenfassung) wahr,
              // statt den neuen Eintrag ans Ende zu hängen.
              laden().catch(() =>
                setFehler(
                  "Das Archiv antwortet nicht. Bitte erneut versuchen.",
                ),
              );
              setErfolg("Auftritt angelegt.");
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

      <div className="auftritte-bereich">
        {/* Die Jahresleiste ist die Navigation des Abschnitts: echte
            Auswahl über die Adresse, Auftritte ohne Datum zuletzt. */}
        <nav aria-label="Auftritte nach Jahr">
          <ul className="auftritte-jahre">
            <li>
              <button
                type="button"
                aria-pressed={jahr === null}
                disabled={jahr === null}
                onClick={() => jahrWaehlen(null)}
              >
                Alle
              </button>
            </li>
            {jahre.map((eintrag) => {
              // Die Gruppe ohne Datum wählt „ohne", die Jahreszahlen
              // wählen sich über ihre Zahl.
              const gewaehlt =
                eintrag.year === null ? jahr === "ohne" : jahr === eintrag.year;
              return (
                <li key={eintrag.year ?? "ohne"}>
                  <button
                    type="button"
                    aria-pressed={gewaehlt}
                    disabled={gewaehlt}
                    onClick={() =>
                      jahrWaehlen(eintrag.year === null ? "ohne" : eintrag.year)
                    }
                  >
                    {eintrag.year === null ? "Ohne Jahr" : eintrag.year}{" "}
                    <span className="auftritte-jahr-anzahl">
                      ({eintrag.count})
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        </nav>

        <section
          aria-labelledby="auftritte-verzeichnis-titel"
          className="auftritte-liste"
        >
          <h2 id="auftritte-verzeichnis-titel">Auftritte</h2>
          {auftritte === null ? (
            <p aria-live="polite" className="auth-statuszeile">
              Auftritte werden geladen …
            </p>
          ) : auftritte.length === 0 ? (
            hatJahresFilter ? (
              <p>Keine Auftritte in dieser Auswahl.</p>
            ) : (
              <p className="hinweis-block">Noch keine Auftritte erfasst.</p>
            )
          ) : (
            <ul className="auftritte-einträge">
              {auftritte.map((auftritt) => (
                <li key={auftritt.id} className="auftritte-eintrag">
                  <p
                    className="auftritt-datum"
                    data-unbestimmt={
                      auftritt.datePrecision === "unknown" ? "true" : undefined
                    }
                  >
                    {auftritt.dateDisplay}
                  </p>
                  <div className="auftritte-inhalt">
                    <p className="auftritte-art">
                      {auftrittArtName(auftritt.kind)}
                    </p>
                    <h3>
                      {auftritt.published ? (
                        <Link
                          href={`/auftritt/?id=${encodeURIComponent(auftritt.id)}`}
                        >
                          {auftritt.title}
                        </Link>
                      ) : (
                        auftritt.title
                      )}
                    </h3>
                    {auftritt.venue && (
                      <p className="auftritt-ort">{auftritt.venue}</p>
                    )}
                    {editor && (
                      <p className="lieder-status">
                        {auftritt.published ? "Veröffentlicht" : "Entwurf"}
                      </p>
                    )}
                    {editor && (
                      <div className="auftritte-aktionen">
                        <button
                          type="button"
                          className="knopf-leise"
                          aria-expanded={bearbeitet === auftritt.id}
                          aria-controls={`auftritt-bearbeiten-${auftritt.id}`}
                          disabled={aktionBusy !== ""}
                          onClick={() =>
                            setBearbeitet(
                              bearbeitet === auftritt.id ? null : auftritt.id,
                            )
                          }
                        >
                          {bearbeitet === auftritt.id
                            ? "Bearbeiten schließen"
                            : "Bearbeiten"}
                        </button>
                        {auftritt.published ? (
                          <button
                            type="button"
                            disabled={aktionBusy !== ""}
                            onClick={() =>
                              auftrittAktion(
                                `unpublish:${auftritt.id}`,
                                `/api/events/${encodeURIComponent(auftritt.id)}/unpublish`,
                                "Auftritt zurückgezogen. Er ist für Mitglieder nicht mehr sichtbar.",
                              )
                            }
                          >
                            {aktionBusy === `unpublish:${auftritt.id}`
                              ? "Wird zurückgezogen …"
                              : "Zurückziehen"}
                          </button>
                        ) : (
                          <button
                            type="button"
                            disabled={aktionBusy !== ""}
                            onClick={() =>
                              auftrittAktion(
                                `publish:${auftritt.id}`,
                                `/api/events/${encodeURIComponent(auftritt.id)}/publish`,
                                "Auftritt veröffentlicht. Mitglieder sehen ihn ab sofort.",
                              )
                            }
                          >
                            {aktionBusy === `publish:${auftritt.id}`
                              ? "Wird veröffentlicht …"
                              : "Veröffentlichen"}
                          </button>
                        )}
                      </div>
                    )}
                    {editor && (
                      <div
                        id={`auftritt-bearbeiten-${auftritt.id}`}
                        className="auftritt-bearbeiten auth-karte"
                        hidden={bearbeitet !== auftritt.id}
                      >
                        {bearbeitet === auftritt.id && (
                          <AuftrittFormular
                            auftritt={auftritt}
                            absendenText="Änderungen speichern"
                            onAbbrechen={() => setBearbeitet(null)}
                            onSuccess={(gespeichert) => {
                              ersetzeAuftritt(gespeichert);
                              setErfolg("Änderungen gespeichert.");
                              setHinweis("");
                              setBearbeitet(null);
                            }}
                          />
                        )}
                      </div>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}
