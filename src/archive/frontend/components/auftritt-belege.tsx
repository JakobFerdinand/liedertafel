"use client";

// ARC-028: Aufführungsnachweise des Auftritts — der Lesesaal liest die
// Belege als geordnete Reihe (Titel, Standmarke, Fassungskette), die
// Redaktion liest dieselbe Reihe ausführlicher (Quellenangabe,
// Erfassungszeitpunkt) und hält die Zeilenwerkzeuge bereit: Erfassen
// (Lied und Fassung aus dem Katalog, Nachweisart, Quellenangabe mit
// Wiederholungsschlüssel), Bearbeiten (inline, mit rowVersion) und
// Löschen (einmalige Bestätigung in der Zeile). Nach jedem Erfolg lädt
// der Eltern-Nachlauf den frischen Stand; veraltete Stände laden ihn
// ebenfalls und erklären die Ersetzung.
//
// Die Songauflösung folgt dem Programm (components/auftritt-programm.tsx):
// Lieddetails kommen aus dem Katalog, die Titelzeile liest sich ehrlich
// ohne Erfindung („Ohne Titel“), die Fassungsangabe aus der geladenen
// Kette oder als „Fassung unbekannt“. Ältere Auftrittsantworten ohne
// Nachweisfeld bleiben zulässig (Toleranz wie bei documents/programme).
//
// ARC-028, dritter Streifen: die Mitglied-Zeilen tragen tiefe Leseporte
// auf die Liedseite (Rezeptur programmPunktUrl — mit bekannter Kette
// fahren Fassung und Version in der Adresse, „Fassung unbekannt“ weist
// nur aufs Lied). Unter der Rubriküberschrift liest ein stilles Satzpaar
// ehrlich, was Bestätigt und Programmangabe unterscheidet — nur dann,
// wenn Nachweise stehen.

import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { FassungsWahl } from "@/components/fassungs-wahl";
import { problemTitel } from "@/lib/assets";
import {
  type AuftrittDetailsMitBelegen,
  aendereNachweis,
  loescheNachweis,
  type Nachweis,
  type NachweisAenderung,
  type NachweisEditor,
  nachweisUrl,
  posteNachweis,
  publishedAtText,
} from "@/lib/events";
import { fetchSong, fetchSongSearch, type LiedDetails } from "@/lib/songs";
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

const liedLadeFehler =
  "Ein Lied konnte nicht geladen werden. Die Fassungsauswahl bleibt leer.";

const katalogFehler = "Der Katalog antwortet nicht. Bitte erneut versuchen.";

// ARC-029: Nachweise der Programmbestätigung werden dort geändert; der
// Server verweigert das Löschen mit dieser Meldung (409).
const gehoertZurBestaetigung =
  "Dieser Nachweis gehört zur Programmbestätigung und wird dort geändert.";

const veralteteAenderung =
  "Der Nachweis wurde zwischenzeitlich geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.";

/** Titelzeile je Nachweis: geladenes Lied, ehrlich beim Laden, „Ohne Titel“
    als ehrlicher Fallback (wie die Programmzeilen ihn kennen). */
function titelVon(
  lied: LiedDetails | "fehler" | undefined,
  laeuft: boolean,
): string {
  if (lied && lied !== "fehler") return lied.title;
  return laeuft ? "Lied wird geladen …" : "Ohne Titel";
}

/**
 * Fassungsangabe der Zeile: ohne Ids oder ohne auflösbare Kette bleibt es
 * beim ehrlichen „Fassung unbekannt“, nichts wird erfunden. Solange das
 * Lied noch lädt, bleibt die Angabe leer — die Zeile meldet das Laden
 * selbst.
 */
function fassungAngabe(
  beleg: Nachweis,
  lied: LiedDetails | "fehler" | undefined,
): string {
  if (!beleg.musicalVersionId || !beleg.arrangementId)
    return "Fassung unbekannt";
  if (!lied || lied === "fehler") return "";
  const arrangement = lied.arrangements.find(
    (probe) => probe.id === beleg.arrangementId,
  );
  const fassung = arrangement?.musicalVersions.find(
    (probe) => probe.id === beleg.musicalVersionId,
  );
  if (!arrangement || !fassung) return "Fassung unbekannt";
  return [
    arrangement.voiceConfiguration,
    arrangement.label,
    fassung.label,
    fassung.musicalKey ? `Tonart: ${fassung.musicalKey}` : null,
  ]
    .filter((teil): teil is string => teil !== null)
    .join(" · ");
}

/** Standmarke der Zeile: Bestätigung oder ehrlicher Programmhinweis;
    ein unbekannter Wert liest sich still ohne Marke. */
function markeVon(evidenceStatus: string): string | null {
  if (evidenceStatus === "confirmed") return "Bestätigt";
  if (evidenceStatus === "mention") return "Programmangabe";
  return null;
}

/** Meta-Zeile der Anzeige: Standmarke und Fassungsangabe als Leiter. */
function standZeile(
  beleg: Nachweis,
  lied: LiedDetails | "fehler" | undefined,
): string {
  return [markeVon(beleg.evidenceStatus), fassungAngabe(beleg, lied)]
    .filter((teil): teil is string => teil !== null)
    .join(" · ");
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
  lied,
  liedLaeuft,
}: {
  beleg: Nachweis;
  stelle: number;
  lied: LiedDetails | "fehler" | undefined;
  liedLaeuft: boolean;
}) {
  // Die Standzeile liest sich einmal: Marke und Fassungsangabe getrennt
  // gehalten, zum Lesen verbunden — unbekannte und noch ladende Teile
  // erzeugen keinen hängenden Leiterstrich.
  const marke = markeVon(beleg.evidenceStatus);
  const fassung = fassungAngabe(beleg, lied);
  const stand = [marke, fassung]
    .filter((teil): teil is string => teil !== null && teil !== "")
    .join(" · ");
  return (
    <li className="nachweis-eintrag">
      <h4 className="nachweis-titel">
        <span className="nachweis-nummer" aria-hidden="true">
          {stelle}.
        </span>{" "}
        {/* Tiefer Leseport wie im Programm: mit bekannter Kette fahren
            Fassung und Version in der Adresse, „Fassung unbekannt“ weist
            nur aufs Lied (nachweisUrl, Vertragsprache). */}
        <Link href={nachweisUrl(beleg)}>{titelVon(lied, liedLaeuft)}</Link>
      </h4>
      {stand && (
        <p className="nachweis-info">
          {marke && (
            <span className="nachweis-marke" data-art={beleg.evidenceStatus}>
              {marke}
            </span>
          )}
          {fassung && <span>{marke ? ` · ${fassung}` : fassung}</span>}
        </p>
      )}
    </li>
  );
}

// ─── Fassungsauswahl mit ehrlichem Leerstand ─────────────────────────

/** Fassungswahl der Werkbank: die Katalog-Fassungswahl plus die
    ausdrückliche „Fassung unbekannt“-Stelle — der leere Kettenzustand
    bleibt erstklassig wählbar. Beim Ladebitrag meldet sich der Stand. */
function Fassungsauswahl({
  lied,
  versionId,
  onFassung,
  onErneutVersuchen,
}: {
  lied: LiedDetails | "fehler" | undefined;
  versionId: string | null;
  onFassung: (versionId: string | null) => void;
  onErneutVersuchen: () => void;
}) {
  const arrangementId =
    lied && lied !== "fehler"
      ? (lied.arrangements.find((probe) =>
          probe.musicalVersions.some((fassung) => fassung.id === versionId),
        )?.id ?? "")
      : "";
  return (
    <div className="belege-fassung">
      <p className="belege-fassung-titel">Fassung</p>
      {lied === "fehler" ? (
        <div>
          <p className="feld-hinweis">{liedLadeFehler}</p>
          <button
            type="button"
            className="knopf-leise"
            onClick={onErneutVersuchen}
          >
            Erneut versuchen
          </button>
        </div>
      ) : lied ? (
        <>
          <ul className="fassungs-liste">
            <li>
              <button
                type="button"
                aria-pressed={versionId === null}
                onClick={() => onFassung(null)}
              >
                Fassung unbekannt
              </button>
            </li>
          </ul>
          <FassungsWahl
            lied={lied}
            arrangementId={arrangementId}
            versionId={versionId ?? ""}
            onSelect={(_arrangementId, gewaehlt) => onFassung(gewaehlt)}
          />
        </>
      ) : (
        <p className="feld-hinweis">Lied wird geladen …</p>
      )}
    </div>
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

// ─── Liedauswahl aus dem Katalog (Erfassungsformular) ────────────────

// Liedsuche nach der Rezeptur der Programm-Werkbank: jede neue Suche
// bricht die vorige ab, ältere Antworten dürfen jüngere nicht
// überschreiben. Eigene Kennzeichen, damit Programm und Nachweise nicht
// um dieselben Feld-Ids streiten.
function BelegLiedWahl({
  onGewaehlt,
}: {
  onGewaehlt: (songId: string, titel: string) => void;
}) {
  const [suche, setSuche] = useState("");
  const [ergebnisse, setErgebnisse] = useState<
    Array<{ id: string; title: string }>
  >([]);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);
  const laufNummer = useRef(0);
  const laufendeAbbruch = useRef<AbortController | null>(null);
  const lebt = useRef(true);

  useEffect(() => {
    lebt.current = true;
    return () => {
      lebt.current = false;
      laufendeAbbruch.current?.abort();
    };
  }, []);

  async function suchen(event: React.FormEvent) {
    event.preventDefault();
    const nummer = laufNummer.current + 1;
    laufNummer.current = nummer;
    laufendeAbbruch.current?.abort();
    const abbruch = new AbortController();
    laufendeAbbruch.current = abbruch;
    setBusy(true);
    setFehler("");
    try {
      const antwort = await fetchSongSearch(
        suche.trim() || null,
        1,
        abbruch.signal,
      );
      if (!lebt.current || nummer !== laufNummer.current) return;
      setErgebnisse(
        antwort.songs.map((eintrag) => ({
          id: eintrag.id,
          title: eintrag.title,
        })),
      );
      if (antwort.songs.length === 0) setFehler("Kein Lied gefunden.");
    } catch {
      // Abgebrochene oder überholte Antworten ändern nichts mehr.
      if (!lebt.current || nummer !== laufNummer.current) return;
      setFehler(katalogFehler);
    } finally {
      if (lebt.current && nummer === laufNummer.current) setBusy(false);
    }
  }

  return (
    <form className="belege-lied-wahl" onSubmit={(event) => void suchen(event)}>
      <label htmlFor="belege-lied-suche">Lied für den Nachweis suchen</label>
      <div className="belege-lied-suche-reihe">
        <input
          id="belege-lied-suche"
          type="search"
          value={suche}
          onChange={(event) => setSuche(event.target.value)}
        />
        <button type="submit" disabled={busy}>
          {busy ? "Wird gesucht …" : "Suchen"}
        </button>
      </div>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {ergebnisse.length > 0 && (
        <div className="belege-lied-treffer">
          {ergebnisse.map((eintrag) => (
            <button
              key={eintrag.id}
              type="button"
              className="knopf-leise-rahmen"
              onClick={() => {
                onGewaehlt(eintrag.id, eintrag.title);
                setErgebnisse([]);
                setSuche("");
              }}
            >
              {eintrag.title}
            </button>
          ))}
        </div>
      )}
    </form>
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
  const [songId, setSongId] = useState<string | null>(null);
  const [songTitel, setSongTitel] = useState("");
  const [versionId, setVersionId] = useState<string | null>(null);
  const [status, setStatus] = useState("");
  const [quelle, setQuelle] = useState("");
  const [formLied, setFormLied] = useState<LiedDetails | "fehler" | null>(null);
  // Ein neuer Versuch für ein fehlgeschlagenes Lied: der Abruf läuft
  // erneut, die Wirkung zählt den Lauf.
  const [versuch, setVersuch] = useState(0);
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");

  // Ein erneuter Versuch führt die Wirkung erneut aus (Rezeptur
  // auftritt-detail.tsx); im Hintergrund wird nicht nachgeladen.
  useEffect(() => {
    void versuch;
    if (!songId) {
      setFormLied(null);
      return undefined;
    }
    setFormLied(null);
    const abbruch = new AbortController();
    fetchSong(songId, abbruch.signal)
      .then((lied) => {
        if (!abbruch.signal.aborted) setFormLied(lied);
      })
      .catch(() => {
        if (!abbruch.signal.aborted) setFormLied("fehler");
      });
    return () => abbruch.abort();
  }, [songId, versuch]);

  function liedWaehlen(gewaehlt: string, titel: string) {
    if (gewaehlt === songId) return;
    setSongId(gewaehlt);
    setSongTitel(titel);
    setVersionId(null);
  }

  function liedErneutVersuchen() {
    setFormLied(null);
    setVersuch((zahl) => zahl + 1);
  }

  async function erfassen() {
    if (!songId) {
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
        songId,
        evidenceStatus: status,
        ...(versionId ? { musicalVersionId: versionId } : {}),
        ...(quelleBeschnitten ? { sourceNote: quelleBeschnitten } : {}),
        idempotencyKey: wiederholungsschluessel,
      });
      // Die Antwort trägt den neuen Nachweis; der Eltern-Nachlauf holt
      // den frischen Stand und der Schlüssel wandert für die nächste
      // bewusste Erfassung weiter.
      setWiederholungsschluessel(crypto.randomUUID());
      setSongId(null);
      setVersionId(null);
      setStatus("");
      setQuelle("");
      setFormLied(null);
      onErfasst();
    } catch (ursache) {
      setFehler(await problemTitel(ursache, ersatzFehler));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="belege-erfassung-formular">
      {songId === null ? (
        <BelegLiedWahl onGewaehlt={liedWaehlen} />
      ) : (
        <>
          <p className="belege-gewaehlt-lied">
            Gewähltes Lied: <span>{songTitel}</span>
          </p>
          <div className="noten-aktionen">
            <button
              type="button"
              className="knopf-leise"
              disabled={busy}
              onClick={() => {
                setSongId(null);
                setVersionId(null);
                setFormLied(null);
              }}
            >
              Anderes Lied wählen
            </button>
          </div>
          <Fassungsauswahl
            lied={formLied ?? undefined}
            versionId={versionId}
            onFassung={setVersionId}
            onErneutVersuchen={liedErneutVersuchen}
          />
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

/** Bearbeitungsformular einer Zeile: Nachweisart, Fassungskette und
    Quellenangabe als Entwurf; der PATCH trägt nur die geänderten Felder
    (abwesend = unverändert, explizit null räumt ab) und die frische
    rowVersion-Ankerung. Veraltete Stände laden den frischen Stand und
    bitten um Nacharbeit, der Entwurf bleibt in der offenen Zeile stehen. */
function BelegBearbeitung({
  beleg,
  lied,
  onMeldung,
  onFertig,
  onGelungen,
  aktualisieren,
  onErneutVersuchen,
}: {
  beleg: NachweisEditor;
  lied: LiedDetails | "fehler" | undefined;
  onMeldung: (meldung: string) => void;
  onFertig: () => void;
  onGelungen: () => void;
  aktualisieren: () => void;
  onErneutVersuchen: () => void;
}) {
  const [status, setStatus] = useState(beleg.evidenceStatus);
  const [versionId, setVersionId] = useState<string | null>(
    beleg.musicalVersionId,
  );
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
    if (versionId === null) {
      // Nur ein ausdrücklich gewähltes „Fassung unbekannt“ reist als null;
      // unveränderte Ketten bleiben abwesend (Vertragssprache).
      if (beleg.musicalVersionId !== null) aenderung.musicalVersionId = null;
    } else if (versionId !== beleg.musicalVersionId) {
      aenderung.musicalVersionId = versionId;
    }
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
      <Fassungsauswahl
        lied={lied}
        versionId={versionId}
        onFassung={setVersionId}
        onErneutVersuchen={onErneutVersuchen}
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
  lied,
  liedFehler,
  liedLaeuft,
  onErneutVersuchen,
  onBearbeitet,
  onGeloescht,
  aktualisieren,
}: {
  beleg: NachweisEditor;
  stelle: number;
  lied: LiedDetails | "fehler" | undefined;
  liedFehler: string;
  liedLaeuft: boolean;
  onErneutVersuchen: () => void;
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

  const stand = standZeile(beleg, lied);
  return (
    <li className="belege-zeile">
      <div className="belege-zeile-kopf">
        <span className="belege-zeile-nummer" aria-hidden="true">
          {stelle}.
        </span>
        {/* Dieselbe Tiefe wie im Lesesaal: die Werkzeile weist mit denselben
            Parametern auf die Liedseite; Werkzeuge bleiben Werkzeuge. */}
        <Link className="belege-zeile-titel" href={nachweisUrl(beleg)}>
          {titelVon(lied, liedLaeuft)}
        </Link>
        {/* Eine ankündigende Stelle je Zeile: der Fehler läuft hier mit
            und wiederholt sich nicht über den ganzen Bereich. */}
        <span className="material-datei-status" aria-live="polite">
          {liedFehler || (liedLaeuft ? "wird geladen …" : "")}
        </span>
      </div>
      {stand && <p className="nachweis-info">{stand}</p>}
      <p className="belege-quelle">{quelleText(beleg)}</p>
      <p className="belege-erfasst">
        Erfasst: {publishedAtText(beleg.capturedAt)}
      </p>
      {liedFehler && (
        <div className="noten-aktionen">
          <button
            type="button"
            className="knopf-leise"
            onClick={onErneutVersuchen}
          >
            Erneut versuchen
          </button>
        </div>
      )}
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
            lied={lied}
            onMeldung={setZeilenFehler}
            onFertig={() => setBearbeitet(false)}
            onGelungen={onBearbeitet}
            aktualisieren={aktualisieren}
            onErneutVersuchen={onErneutVersuchen}
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
  const [lieder, setLieder] = useState<Record<string, LiedDetails | "fehler">>(
    {},
  );
  const [liedFehler, setLiedFehler] = useState<Record<string, string>>({});

  const liedIds = useMemo(
    () => [...new Set(belege.map((beleg) => beleg.songId))],
    [belege],
  );

  // Lieddetails je betroffenem Nachweis (Titel, Fassungskette, Wahlwerk);
  // schon geladene Lieder bleiben im Bestand, nur fehlende Ids werden
  // geholt. Eine fehlerhafte Abfrage markiert ihr Lied als „fehler“ und
  // wartet auf „Erneut versuchen“, statt sich endlos zu wiederholen.
  useEffect(() => {
    const fehlende = liedIds.filter((id) => !(id in lieder));
    if (fehlende.length === 0) return;
    const abbruch = new AbortController();
    void Promise.all(
      fehlende.map(async (id) => {
        try {
          return [id, await fetchSong(id, abbruch.signal)] as const;
        } catch {
          return [id, "fehler"] as const;
        }
      }),
    ).then((paare) => {
      if (abbruch.signal.aborted) return;
      setLieder((bisher) => ({
        ...bisher,
        ...Object.fromEntries(paare),
      }));
      setLiedFehler((bisher) => {
        const stand = { ...bisher };
        for (const [id, ergebnis] of paare) {
          if (ergebnis === "fehler") stand[id] = liedLadeFehler;
          else delete stand[id];
        }
        return stand;
      });
    });
    return () => abbruch.abort();
  }, [liedIds, lieder]);

  function liedErneutVersuchen(id: string) {
    setLieder((bisher) => {
      const stand = { ...bisher };
      delete stand[id];
      return stand;
    });
    setLiedFehler((bisher) => {
      const stand = { ...bisher };
      delete stand[id];
      return stand;
    });
  }

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
              lied={lieder[beleg.songId]}
              liedFehler={liedFehler[beleg.songId] ?? ""}
              liedLaeuft={!(beleg.songId in lieder)}
              onErneutVersuchen={() => liedErneutVersuchen(beleg.songId)}
              onBearbeitet={() => setErfolg("Änderungen gespeichert.")}
              onGeloescht={() => setErfolg("Nachweis gelöscht.")}
              aktualisieren={aktualisieren}
            />
          ))}
        </ol>
      ) : (
        <ol className="nachweise-liste">
          {belege.map((beleg, index) => (
            <NachweisEintrag
              key={beleg.id}
              beleg={beleg}
              stelle={index + 1}
              lied={lieder[beleg.songId]}
              liedLaeuft={!(beleg.songId in lieder)}
            />
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
