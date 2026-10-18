"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { DateiHochladen } from "@/components/datei-hochladen";
import { DateiListe } from "@/components/datei-liste";
import { HistorieAufnahmen } from "@/components/historie-aufnahmen";
import { LiedFormular } from "@/components/lied-formular";
import { LiedHistorie } from "@/components/lied-historie";
import { LiedKopf } from "@/components/lied-kopf";
import { fetchMe, type MeResponse } from "@/lib/auth";
import { fetchLied, type LiedDatei, type LiedMitDateien } from "@/lib/lieder";

/**
 * Die Lied-Seite (#166). Sie lädt das Lied und setzt die Teile zusammen; jeder
 * Teil hat seine eigene Komponente: LiedKopf (Angaben), DateiListe (Dateien,
 * darin NotenAnsicht), DateiHochladen (nur Redaktion), LiedFormular (nur
 * Redaktion). Folgetickets hängen ihre Teile mit je einer Zeile unten ein.
 */
export function LiedDetail() {
  const suchParameter = useSearchParams();
  const id = suchParameter.get("id");
  const [me, setMe] = useState<MeResponse | null>(null);
  const [lied, setLied] = useState<LiedMitDateien | null>(null);
  const [nichtGefunden, setNichtGefunden] = useState(false);
  const [fehler, setFehler] = useState("");
  const [erfolg, setErfolg] = useState("");
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
        const details = await fetchLied(id, signal);
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

  // Nach einem Upload wird das Lied neu geladen, damit die Dateiliste stimmt;
  // eine fehlgeschlagene Aktualisierung bricht die Ansicht nicht ab.
  const dateienAktualisieren = useCallback(
    async (_datei?: LiedDatei) => {
      if (!id) return;
      try {
        setLied(await fetchLied(id));
      } catch {
        // Der nächste Aufruf der Seite lädt neu.
      }
    },
    [id],
  );

  useEffect(() => {
    const abort = new AbortController();
    // Ein erneuter Versuch führt die Wirkung erneut aus.
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

  if (fehler) {
    return (
      <div className="detail-zustand" aria-live="polite">
        <h1>Lied nicht verfügbar</h1>
        <p>{fehler}</p>
        <button
          type="button"
          onClick={() => {
            setFehler("");
            setVersuch(versuch + 1);
          }}
        >
          Erneut versuchen
        </button>
      </div>
    );
  }
  if (me === null) {
    return (
      <div className="detail-zustand" aria-live="polite">
        <h1>Lied wird geladen …</h1>
      </div>
    );
  }
  if (!me.authenticated) {
    return (
      <div>
        <h1>Anmeldung erforderlich</h1>
        <p>Bitte anmelden, um das Lied zu sehen.</p>
        <Link href="/anmelden/">Anmelden</Link>
      </div>
    );
  }
  if (nichtGefunden) {
    return (
      <div aria-live="polite">
        <h1>Lied nicht gefunden</h1>
        <p>
          Dieses Lied wurde nicht gefunden. Die Adresse ist nicht mehr gültig.
        </p>
        <Link href="/lieder/">Zum Liederkatalog</Link>
      </div>
    );
  }
  if (lied === null) {
    return (
      <div className="detail-zustand" aria-live="polite">
        <h1>Lied wird geladen …</h1>
      </div>
    );
  }

  return (
    <div className="lied-detail">
      <LiedKopf lied={lied} />

      <section className="noten-bereich materialien" aria-label="Dateien">
        <h3 id="material-titel">Dateien</h3>
        <DateiListe
          dateien={lied.dateien}
          liedId={editor ? lied.id : undefined}
          onGeaendert={editor ? () => void dateienAktualisieren() : undefined}
        />
      </section>

      <LiedHistorie
        liedId={lied.id}
        isEditor={Boolean(editor)}
        erweiterung={(zeile) =>
          (zeile.recordings ?? []).length > 0 ? (
            <HistorieAufnahmen
              zeile={zeile}
              liedTitel={lied.title}
              isEditor={Boolean(editor)}
            />
          ) : null
        }
      />

      {editor && (
        <DateiHochladen
          liedId={lied.id}
          besetzung={lied.voiceConfiguration}
          onGespeichert={() => void dateienAktualisieren()}
        />
      )}

      {editor && (
        <details
          className="auth-karte lied-bearbeiten"
          aria-labelledby="lied-bearbeiten-titel"
        >
          <summary>
            <h2 id="lied-bearbeiten-titel">Lied bearbeiten</h2>
            <span className="lied-bearbeiten-umschalter" aria-hidden="true">
              <span className="lied-bearbeiten-auf">Ausklappen</span>
              <span className="lied-bearbeiten-zu">Einklappen</span>
            </span>
          </summary>
          <LiedFormular
            key={lied.rowVersion}
            lied={lied}
            absendenText="Änderungen speichern"
            onSuccess={(gespeichert) => {
              setLied(gespeichert);
              setErfolg("Änderungen gespeichert.");
            }}
          />
        </details>
      )}

      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
    </div>
  );
}
