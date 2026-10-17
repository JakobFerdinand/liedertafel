"use client";

// ARC-028: Aufführungsnachweise des Auftritts — der Lesesaal liest die
// Belege als geordnete Reihe (Titel, Standmarke), die
// Redaktion liest dieselbe Reihe ausführlicher (Quellenangabe,
// Erfassungszeitpunkt) und hält die Zeilenwerkzeuge bereit: Erfassen
// (Lied aus dem Katalog, Nachweisart, Quellenangabe mit
// Wiederholungsschlüssel), Bearbeiten (inline, mit rowVersion) und
// Löschen (einmalige Bestätigung in der Zeile). Nach jedem Erfolg lädt
// der Eltern-Nachlauf den frischen Stand; veraltete Stände laden ihn
// ebenfalls und erklären die Ersetzung.
//
// Ein Nachweis nennt ein Lied (#170); der Titel kommt mit dem Nachweis,
// die Titelzeile liest sich ehrlich ohne Erfindung („Ohne Titel“). Ältere
// Auftrittsantworten ohne Nachweisfeld bleiben zulässig (Toleranz wie bei
// documents/programme). Die Zeilen tragen tiefe Leseporte auf die
// Liedseite. Unter der Rubriküberschrift liest ein stilles Satzpaar
// ehrlich, was Bestätigt und Programmangabe unterscheidet — nur dann,
// wenn Nachweise stehen.

import Link from "next/link";
import { useMemo, useState } from "react";
import { LiedWahl } from "@/components/lied-wahl";
import { problemTitel } from "@/lib/assets";
import {
  type AuftrittDetailsMitBelegen,
  aendereNachweis,
  liedUrl,
  loescheNachweis,
  type Nachweis,
  type NachweisAenderung,
  type NachweisEditor,
  posteNachweis,
  publishedAtText,
} from "@/lib/events";
import { ZeitmarkenVorhanden } from "@/lib/zeitmarken";

// Deutsche Vertragstexte (Konstanten in PerformanceEndpoints.cs): die
// Pflicht- und Längenregeln gelten hier wie dort — der Server prüft sie
// erneut.
const quelleErforderlich =
  "Ein unverifizierter Programmhinweis braucht eine Quellenangabe.";
const quelleZuLang = "Die Quellenangabe ist zu lang.";
const nachweiswertUngueltig = "Der Nachweiswert ist ungültig.";

// Feldgrenze der Quellenangabe (Nachweis-Schema, serverseitig gespiegelt).
const quelleMaximal = 2000;

const ersatzFehler = "Das hat nicht geklappt. Bitte erneut versuchen.";

// ARC-029: Nachweise der Programmbestätigung werden dort geändert; der
// Server verweigert das Löschen mit dieser Meldung (409).
const gehoertZurBestaetigung =
  "Dieser Nachweis gehört zur Programmbestätigung und wird dort geändert.";

const veralteteAenderung =
  "Der Nachweis wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

/** Titelzeile je Nachweis: das Lied des Nachweises, „Ohne Titel“ als
    ehrlicher Fallback (wie die Programmzeilen ihn kennen). */
function titelVon(beleg: Nachweis): string {
  return beleg.liedTitle ?? "Ohne Titel";
}

/** Standmarke der Zeile: Bestätigung oder ehrlicher Programmhinweis;
    ein unbekannter Wert liest sich still ohne Marke. */
function markeVon(evidenceStatus: string): string | null {
  if (evidenceStatus === "confirmed") return "Bestätigt";
  if (evidenceStatus === "mention") return "Programmangabe";
  return null;
}

/** Meta-Zeile der Anzeige: die Standmarke. */
function standZeile(beleg: Nachweis): string {
  return markeVon(beleg.evidenceStatus) ?? "";
}

// Quellenangabe der Zeile: die überlieferte Angabe oder der ehrliche
// Leerstand (serverseitig tragen nur bestätigte Aufführungen keinen Text —
// Programmangaben bringen ihre Pflichtangabe mit).
function quelleText(beleg: NachweisEditor): string {
  const quelle = beleg.sourceNote?.trim();
  return quelle ? `Quellenangabe: ${quelle}` : "Keine Quellenangabe.";
}

// ─── Lesesaal: die Zeile der Mitglieder ──────────────────────────────

function NachweisEintrag({
  beleg,
  stelle,
}: {
  beleg: Nachweis;
  stelle: number;
}) {
  const marke = markeVon(beleg.evidenceStatus);
  const ziel = liedUrl(beleg.liedId);
  return (
    <li className="nachweis-eintrag">
      <h4 className="nachweis-titel">
        <span className="nachweis-nummer" aria-hidden="true">
          {stelle}.
        </span>{" "}
        {ziel ? <Link href={ziel}>{titelVon(beleg)}</Link> : titelVon(beleg)}
      </h4>
      {marke && (
        <p className="nachweis-info">
          <span className="nachweis-marke" data-art={beleg.evidenceStatus}>
            {marke}
          </span>
        </p>
      )}
    </li>
  );
}

// ─── Nachweisart: die Radio-Paarwahl ─────────────────────────────────

function NachweisartWahl({
  value,
  onChange,
  name,
}: {
  value: string;
  onChange: (wert: string) => void;
  name: string;
}) {
  return (
    <fieldset className="belege-art">
      <legend>Nachweisart</legend>
      <div className="belege-art-auswahl">
        <label>
          <input
            type="radio"
            name={name}
            value="confirmed"
            checked={value === "confirmed"}
            onChange={() => onChange("confirmed")}
          />
          Bestätigte Aufführung
        </label>
        <label>
          <input
            type="radio"
            name={name}
            value="mention"
            checked={value === "mention"}
            onChange={() => onChange("mention")}
          />
          Programmangabe (unbestätigt)
        </label>
      </div>
    </fieldset>
  );
}

// ─── Erfassungsformular: Beleg benennen und einreichen ───────────────

// Der Wiederholungsschlüssel lebt je Formularinstanz: er zeugt mit dem
// Aufklappen und wird nach einem Erfolg neu erzeugt, damit ein bewusster
// zweiter Eintrag nicht als Wiederholung der ersten liest.
function BelegErfassenFormular({
  auftrittId,
  onErfasst,
}: {
  auftrittId: string;
  onErfasst: () => void;
}) {
  const [wiederholungsschluessel, setWiederholungsschluessel] = useState(() =>
    crypto.randomUUID(),
  );
  const [liedId, setLiedId] = useState<string | null>(null);
  const [liedTitel, setLiedTitel] = useState("");
  const [status, setStatus] = useState("");
  const [quelle, setQuelle] = useState("");
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");

  function liedWaehlen(gewaehlt: string, titel: string) {
    if (gewaehlt === liedId) return;
    setLiedId(gewaehlt);
    setLiedTitel(titel);
  }

  async function erfassen() {
    if (!liedId) {
      setFehler("Wähle ein Lied aus dem Katalog.");
      return;
    }
    if (status === "") {
      setFehler(nachweiswertUngueltig);
      return;
    }
    const quelleBeschnitten = quelle.trim();
    if (quelleBeschnitten.length > quelleMaximal) {
      setFehler(quelleZuLang);
      return;
    }
    if (status === "mention" && !quelleBeschnitten) {
      setFehler(quelleErforderlich);
      return;
    }
    setBusy(true);
    setFehler("");
    try {
      await posteNachweis(auftrittId, {
        liedId,
        evidenceStatus: status,
        ...(quelleBeschnitten ? { sourceNote: quelleBeschnitten } : {}),
        idempotencyKey: wiederholungsschluessel,
      });
      // Die Antwort trägt den neuen Nachweis; der Eltern-Nachlauf holt
      // den frischen Stand und der Schlüssel wandert für die nächste
      // bewusste Erfassung weiter.
      setWiederholungsschluessel(crypto.randomUUID());
      setLiedId(null);
      setStatus("");
      setQuelle("");
      onErfasst();
    } catch (ursache) {
      setFehler(await problemTitel(ursache, ersatzFehler));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="belege-erfassung-formular">
      {liedId === null ? (
        <LiedWahl
          klasse="belege"
          kennung="belege-lied-suche"
          beschriftung="Lied für den Nachweis suchen"
          onGewaehlt={liedWaehlen}
        />
      ) : (
        <>
          <p className="belege-gewaehlt-lied">
            Gewähltes Lied: <span>{liedTitel}</span>
          </p>
          <div className="noten-aktionen">
            <button
              type="button"
              className="knopf-leise"
              disabled={busy}
              onClick={() => setLiedId(null)}
            >
              Anderes Lied wählen
            </button>
          </div>
          <NachweisartWahl
            name="belege-erfassen-art"
            value={status}
            onChange={setStatus}
          />
          <div className="belege-notiz">
            <label htmlFor="belege-erfassen-quelle">Quellenangabe</label>
            <textarea
              id="belege-erfassen-quelle"
              maxLength={quelleMaximal}
              value={quelle}
              onChange={(event) => setQuelle(event.target.value)}
            />
            <p className="feld-hinweis">
              Pflicht bei „Programmangabe (unbestätigt)“, optional bei
              bestätigter Aufführung.
            </p>
          </div>
          {fehler && (
            <output aria-live="polite" className="feld-fehler">
              {fehler}
            </output>
          )}
          <div className="noten-aktionen">
            <button
              type="button"
              disabled={busy}
              onClick={() => void erfassen()}
            >
              {busy ? "Wird erfasst …" : "Nachweis erfassen"}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

// ─── Werkbank-Zeile: dieselbe Reihe plus Quellenangabe und Werkzeuge ─

/** Bearbeitungsformular einer Zeile: Nachweisart und
    Quellenangabe als Entwurf; der PATCH trägt nur die geänderten Felder
    (abwesend = unverändert, explizit null räumt ab) und die frische
    rowVersion-Ankerung. Veraltete Stände laden den frischen Stand und
    bitten um Nacharbeit, der Entwurf bleibt in der offenen Zeile stehen. */
function BelegBearbeitung({
  beleg,
  onMeldung,
  onFertig,
  onGelungen,
  aktualisieren,
}: {
  beleg: NachweisEditor;
  onMeldung: (meldung: string) => void;
  onFertig: () => void;
  onGelungen: () => void;
  aktualisieren: () => void;
}) {
  const [status, setStatus] = useState(beleg.evidenceStatus);
  const [quelle, setQuelle] = useState(beleg.sourceNote ?? "");
  const [busy, setBusy] = useState(false);

  async function speichern() {
    const quelleBeschnitten = quelle.trim();
    if (quelleBeschnitten.length > quelleMaximal) {
      onMeldung(quelleZuLang);
      return;
    }
    if (status === "mention" && !quelleBeschnitten) {
      onMeldung(quelleErforderlich);
      return;
    }
    const aenderung: NachweisAenderung = { rowVersion: beleg.rowVersion };
    if (status !== beleg.evidenceStatus) aenderung.evidenceStatus = status;
    if (quelleBeschnitten !== (beleg.sourceNote ?? "").trim()) {
      aenderung.sourceNote = quelleBeschnitten || null;
    }
    if (Object.keys(aenderung).length === 1) {
      onMeldung("Keine Änderungen vorgenommen.");
      return;
    }
    setBusy(true);
    onMeldung("");
    try {
      await aendereNachweis(beleg.id, aenderung);
      aktualisieren();
      onGelungen();
      onFertig();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        // Veralteter Stand: der frische kommt über aktualisieren(); die
        // Redaktion prüft und wiederholt in der offenen Zeile.
        onMeldung(veralteteAenderung);
        aktualisieren();
        return;
      }
      onMeldung(await problemTitel(ursache, ersatzFehler));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="belege-bearbeitung-inhalt">
      <NachweisartWahl
        name={`belege-bearbeiten-${beleg.id}`}
        value={status}
        onChange={setStatus}
      />
      <div className="belege-notiz">
        <label htmlFor={`belege-bearbeiten-${beleg.id}-quelle`}>
          Quellenangabe
        </label>
        <textarea
          id={`belege-bearbeiten-${beleg.id}-quelle`}
          maxLength={quelleMaximal}
          value={quelle}
          onChange={(event) => setQuelle(event.target.value)}
        />
        <p className="feld-hinweis">
          Pflicht bei „Programmangabe (unbestätigt)“, optional bei bestätigter
          Aufführung.
        </p>
      </div>
      <div className="noten-aktionen">
        <button type="button" disabled={busy} onClick={() => void speichern()}>
          {busy ? "Wird gespeichert …" : "Änderungen speichern"}
        </button>
        <button
          type="button"
          className="knopf-leise"
          disabled={busy}
          onClick={onFertig}
        >
          Abbrechen
        </button>
      </div>
    </div>
  );
}

// ─── Werkbank-Zeile: dieselbe Reihe plus Quellenangabe und Werkzeuge ─

function BelegZeile({
  beleg,
  stelle,
  onBearbeitet,
  onGeloescht,
  aktualisieren,
}: {
  beleg: NachweisEditor;
  stelle: number;
  onBearbeitet: () => void;
  onGeloescht: () => void;
  aktualisieren: () => void;
}) {
  const [bearbeitet, setBearbeitet] = useState(false);
  const [loeschbereit, setLoeschbereit] = useState(false);
  const [loeschBusy, setLoeschBusy] = useState(false);
  const [zeilenFehler, setZeilenFehler] = useState("");

  async function loeschen() {
    setLoeschBusy(true);
    setZeilenFehler("");
    try {
      await loescheNachweis(beleg.id);
      aktualisieren();
      onGeloescht();
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 409) {
        // Der Server nennt den Grund selbst: ein Nachweis der Bestätigung
        // ist kein veralteter Stand.
        const titel = await problemTitel(ursache, veralteteAenderung);
        // Die Sperren des Servers (Programmbestätigung, Zeitmarken in
        // Aufnahmen) nennen ihren Grund selbst und sind kein veralteter Stand.
        setZeilenFehler(
          titel === gehoertZurBestaetigung ||
            titel.startsWith(ZeitmarkenVorhanden)
            ? titel
            : veralteteAenderung,
        );
        aktualisieren();
        return;
      }
      setZeilenFehler(await problemTitel(ursache, ersatzFehler));
    } finally {
      setLoeschBusy(false);
    }
  }

  const stand = standZeile(beleg);
  const ziel = liedUrl(beleg.liedId);
  return (
    <li className="belege-zeile">
      <div className="belege-zeile-kopf">
        <span className="belege-zeile-nummer" aria-hidden="true">
          {stelle}.
        </span>
        {/* Dieselbe Tiefe wie im Lesesaal: die Werkzeile weist mit denselben
            Parametern auf die Liedseite; Werkzeuge bleiben Werkzeuge. */}
        {ziel ? (
          <Link className="belege-zeile-titel" href={ziel}>
            {titelVon(beleg)}
          </Link>
        ) : (
          <span className="belege-zeile-titel">{titelVon(beleg)}</span>
        )}
      </div>
      {stand && <p className="nachweis-info">{stand}</p>}
      <p className="belege-quelle">{quelleText(beleg)}</p>
      <p className="belege-erfasst">
        Erfasst: {publishedAtText(beleg.capturedAt)}
      </p>
      <div className="belege-werkzeuge">
        <button
          type="button"
          className="knopf-leise"
          aria-expanded={bearbeitet}
          aria-controls={`belege-zeile-${beleg.id}-bearbeitung`}
          disabled={loeschBusy}
          onClick={() => setBearbeitet(!bearbeitet)}
        >
          {bearbeitet ? "Bearbeiten schließen" : "Bearbeiten"}
        </button>
        {beleg.confirmationId ? (
          <p className="noten-info belege-bestaetigt">
            Gehört zur Programmbestätigung; änderbar unter „Tatsächlich
            gesungen“.
          </p>
        ) : loeschbereit ? (
          <button
            type="button"
            disabled={loeschBusy}
            onClick={() => void loeschen()}
          >
            Wirklich löschen?
          </button>
        ) : (
          <button
            type="button"
            className="knopf-leise"
            disabled={loeschBusy}
            onClick={() => setLoeschbereit(true)}
          >
            Löschen
          </button>
        )}
      </div>
      {zeilenFehler && (
        <output aria-live="polite" className="feld-fehler">
          {zeilenFehler}
        </output>
      )}
      {/* Der Bearbeiten-Feldzug trägt seine Kennung, solange die Zeile
          steht; der Inhalt öffnet sich erst mit dem Aufklapper. */}
      <div
        className="belege-bearbeitung"
        id={`belege-zeile-${beleg.id}-bearbeitung`}
        hidden={!bearbeitet}
      >
        {bearbeitet && (
          <BelegBearbeitung
            beleg={beleg}
            onMeldung={setZeilenFehler}
            onFertig={() => setBearbeitet(false)}
            onGelungen={onBearbeitet}
            aktualisieren={aktualisieren}
          />
        )}
      </div>
    </li>
  );
}

// ─── Der Bereich ─────────────────────────────────────────────────────

export function AuftrittBelege({
  auftritt,
  isEditor,
  aktualisieren,
}: {
  auftritt: AuftrittDetailsMitBelegen;
  isEditor: boolean;
  aktualisieren: () => void;
}) {
  // Tolerant gegenüber Antworten ohne Nachweisfeld (älterer Stand).
  const belege = useMemo(
    () => auftritt.performances ?? [],
    [auftritt.performances],
  );
  // Redaktionseinbettung (Quellenangabe, Zeitschritte, rowVersion): der
  // Server liefert sie mit Editorrolle (Vertragssprache).
  const editorBelege = isEditor ? (belege as NachweisEditor[]) : [];

  const [erfolg, setErfolg] = useState("");
  return (
    <section
      className="auftritt-abschnitt auftritt-belege"
      aria-labelledby="auftritt-belege-titel"
    >
      <h3 id="auftritt-belege-titel">Aufführungsnachweise</h3>
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {/* Ehrliches Satzpaar für den Lesesaal: was Bestätigt von
          Programmangabe unterscheidet — nur, wenn Nachweise stehen; der
          Leerstand bleibt für sich sprechend. */}
      {!isEditor && belege.length > 0 && (
        <p className="belege-einleitung">
          Bestätigt heißt: der Auftritt ist überliefert. Programmangabe heißt:
          das Lied steht in einer Programmquelle, ohne dass die Aufführung
          gesichert ist.
        </p>
      )}
      {belege.length === 0 ? (
        <p className="auftritt-leer">
          Zu diesem Auftritt sind noch keine Aufführungsnachweise erfasst.
        </p>
      ) : isEditor ? (
        <ol className="belege-zeilen">
          {editorBelege.map((beleg, index) => (
            <BelegZeile
              key={beleg.id}
              beleg={beleg}
              stelle={index + 1}
              onBearbeitet={() => setErfolg("Änderungen gespeichert.")}
              onGeloescht={() => setErfolg("Nachweis gelöscht.")}
              aktualisieren={aktualisieren}
            />
          ))}
        </ol>
      ) : (
        <ol className="nachweise-liste">
          {belege.map((beleg, index) => (
            <NachweisEintrag key={beleg.id} beleg={beleg} stelle={index + 1} />
          ))}
        </ol>
      )}

      {isEditor && (
        <details
          className="belege-erfassung"
          aria-labelledby="belege-erfassung-titel"
        >
          <summary>
            <h4 id="belege-erfassung-titel">Beleg erfassen</h4>
            <span className="belege-erfassung-umschalter" aria-hidden="true">
              <span className="belege-erfassung-auf">Ausklappen</span>
              <span className="belege-erfassung-zu">Einklappen</span>
            </span>
          </summary>
          <fieldset className="belege-erfassung" aria-label="Beleg erfassen">
            <BelegErfassenFormular
              auftrittId={auftritt.id}
              onErfasst={() => {
                setErfolg("Nachweis erfasst.");
                aktualisieren();
              }}
            />
          </fieldset>
        </details>
      )}
    </section>
  );
}
