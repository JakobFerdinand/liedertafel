"use client";

import { useState } from "react";
import { patchAuth, postAuth } from "@/lib/auth";
import type { Auftritt, AuftrittDetails } from "@/lib/events";

// Auftrittstypen des Vertrags mit ihren deutschen Namen; die Auswahl
// startet beim häufigsten Fall.
const arten: Array<[string, string]> = [
  ["concert", "Konzert"],
  ["service", "Gottesdienst"],
  ["wedding", "Hochzeit"],
  ["funeral", "Bestattung"],
  ["festival", "Fest"],
  ["other", "Sonstiger Auftritt"],
];

// Monatsnamen wie der Server sie rendert (kulturinvariant, siehe
// EventDate.MonthNames); der Wert bleibt die Zahl.
const monatsNamen = [
  "Januar",
  "Februar",
  "März",
  "April",
  "Mai",
  "Juni",
  "Juli",
  "August",
  "September",
  "Oktober",
  "November",
  "Dezember",
];

type AuftrittFormularWerte = {
  kind: string;
  title: string;
  jahr: string;
  monat: string;
  tag: string;
  unsicher: boolean;
  venue: string;
  zeit: string;
  hinweise: string;
  quelle: string;
};

const LEER: AuftrittFormularWerte = {
  kind: "concert",
  title: "",
  jahr: "",
  monat: "",
  tag: "",
  unsicher: false,
  venue: "",
  zeit: "",
  hinweise: "",
  quelle: "",
};

export function AuftrittFormular({
  auftritt,
  absendenText,
  onSuccess,
  onAbbrechen,
}: {
  auftritt?:
    | (Auftritt & Partial<Pick<AuftrittDetails, "notes" | "sourceNote">>)
    | null;
  absendenText: string;
  onSuccess: (auftritt: AuftrittDetails, meldung: string) => void;
  onAbbrechen?: () => void;
}) {
  const idPraefix = auftritt ? `auftritt-${auftritt.id}` : "auftritt";
  // Nur die Detailansicht kennt die bisherigen Hinweise und die Quelle;
  // im Verzeichnis bleibt das Feld ohne bekannten Wert, und ein geleertes
  // Feld ändert den bisherigen Wert dort nicht.
  const kenntHinweise = auftritt?.notes !== undefined;
  const kenntQuelle = auftritt?.sourceNote !== undefined;
  const [werte, setWerte] = useState<AuftrittFormularWerte>(
    auftritt
      ? {
          kind: auftritt.kind,
          title: auftritt.title,
          jahr: auftritt.dateYear === null ? "" : String(auftritt.dateYear),
          monat: auftritt.dateMonth === null ? "" : String(auftritt.dateMonth),
          tag: auftritt.dateDay === null ? "" : String(auftritt.dateDay),
          unsicher: auftritt.dateApproximate,
          venue: auftritt.venue ?? "",
          zeit: auftritt.startTime ?? "",
          hinweise: auftritt.notes ?? "",
          quelle: auftritt.sourceNote ?? "",
        }
      : LEER,
  );
  const [titelFehler, setTitelFehler] = useState("");
  const [datumFehler, setDatumFehler] = useState("");
  const [zeitFehler, setZeitFehler] = useState("");
  const [hinweis, setHinweis] = useState("");
  const [busy, setBusy] = useState(false);

  function setzen(
    feld: Exclude<keyof AuftrittFormularWerte, "unsicher">,
    wert: string,
  ) {
    setWerte((bisher) => ({ ...bisher, [feld]: wert }));
  }

  async function speichern(event: React.FormEvent) {
    event.preventDefault();
    setTitelFehler("");
    setDatumFehler("");
    setZeitFehler("");
    setHinweis("");
    const titel = werte.title.trim();
    if (!titel) {
      setTitelFehler("Der Titel ist erforderlich.");
      return;
    }
    // Das Datum ergibt sich aus den ausgefüllten Feldern: nur Jahr,
    // Jahr mit Monat und Jahr mit Monat und Tag sind gültig; nichts
    // ausgefüllt bleibt ehrlich unbekannt. Kein erfundenes Kalenderdatum.
    const jahrText = werte.jahr.trim();
    const monat = werte.monat ? Number.parseInt(werte.monat, 10) : null;
    const tagText = werte.tag.trim();
    const tag = tagText ? Number.parseInt(tagText, 10) : null;
    const jahr = jahrText ? Number.parseInt(jahrText, 10) : null;
    if (
      jahrText &&
      (!/^\d+$/.test(jahrText) || jahr === null || jahr < 1800 || jahr > 2100)
    ) {
      setDatumFehler("Das Jahr liegt außerhalb des möglichen Bereichs.");
      return;
    }
    if (tag !== null && monat === null) {
      setDatumFehler("Das Datum ist unvollständig.");
      return;
    }
    if ((monat !== null || tag !== null) && jahr === null) {
      setDatumFehler("Das Datum ist unvollständig.");
      return;
    }
    if (jahr !== null && monat !== null && tag !== null) {
      const tageImMonat = new Date(jahr, monat, 0).getDate();
      if (Number.isNaN(tag) || tag < 1 || tag > tageImMonat) {
        setDatumFehler("Das Datum passt nicht zum angegebenen Monat.");
        return;
      }
    }
    const zeit = werte.zeit.trim();
    if (zeit && !/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(zeit)) {
      setZeitFehler("Die Uhrzeit muss im Format HH:MM angegeben werden.");
      return;
    }
    setBusy(true);
    try {
      const ort = werte.venue.trim();
      const hinweise = werte.hinweise.trim();
      const quelle = werte.quelle.trim();
      let response: Response;
      if (auftritt) {
        // Die Adresse trägt das Datum als ganzen Block (vollständiger
        // Ersatz, null = unbekannt); bekannte Texte gehen stets mit und
        // ein geleertes Feld räumt sie weg. Unbekannte bisherige Werte
        // (Verzeichnis) werden nur getippt geschickt.
        const body: Record<string, unknown> = {
          kind: werte.kind,
          title: titel,
          venue: ort,
          startTime: zeit,
          date: {
            year: jahr,
            month: monat,
            day: tag,
            approximate: werte.unsicher,
          },
        };
        if (kenntHinweise || hinweise) body.notes = hinweise;
        if (kenntQuelle || quelle) body.sourceNote = quelle;
        response = await patchAuth(
          `/api/events/${encodeURIComponent(auftritt.id)}`,
          body,
        );
      } else {
        response = await postAuth("/api/events", {
          kind: werte.kind,
          title: titel,
          venue: ort ? ort : null,
          dateYear: jahr,
          dateMonth: monat,
          dateDay: tag,
          dateApproximate: werte.unsicher,
          startTime: zeit ? zeit : null,
          notes: hinweise ? hinweise : null,
          sourceNote: quelle ? quelle : null,
        });
      }
      const payload = (await response.json().catch(() => null)) as {
        event?: AuftrittDetails;
      } | null;
      if (!response.ok) {
        const problem = payload as { title?: unknown } | null;
        setHinweis(
          typeof problem?.title === "string" && problem.title
            ? problem.title
            : "Das hat nicht geklappt. Bitte erneut versuchen.",
        );
        return;
      }
      const gespeichert = payload?.event;
      if (gespeichert) {
        onSuccess(
          gespeichert,
          auftritt ? "Änderungen gespeichert." : "Auftritt angelegt.",
        );
        if (!auftritt) setWerte(LEER);
      } else {
        setHinweis("Das hat nicht geklappt. Bitte erneut versuchen.");
      }
    } catch {
      setHinweis(
        "Sicherheitstoken konnte nicht geladen werden. Seite neu laden.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={speichern} noValidate>
      <label htmlFor={`${idPraefix}-typ`}>Auftrittstyp</label>
      <select
        id={`${idPraefix}-typ`}
        value={werte.kind}
        onChange={(event) => setzen("kind", event.target.value)}
      >
        {arten.map(([wert, name]) => (
          <option key={wert} value={wert}>
            {name}
          </option>
        ))}
      </select>
      <label htmlFor={`${idPraefix}-titel`}>Titel</label>
      <input
        id={`${idPraefix}-titel`}
        type="text"
        required
        maxLength={200}
        value={werte.title}
        onChange={(event) => setzen("title", event.target.value)}
        aria-invalid={titelFehler ? true : undefined}
        aria-describedby={`${idPraefix}-titel-fehler`}
      />
      {titelFehler && (
        <p
          id={`${idPraefix}-titel-fehler`}
          role="alert"
          className="feld-fehler"
        >
          {titelFehler}
        </p>
      )}
      <fieldset className="auftritt-datum-felder">
        <legend>Datum</legend>
        <p className="feld-hinweis">
          Nur überlieferte Angaben eintragen; Felder leer lassen, wenn nichts
          überliefert ist – der Auftritt erscheint dann als „Datum unbekannt“.
          Ein Datum ohne Tag und Monat bleibt unsicher.
        </p>
        <div className="auftritt-datum-eingaben">
          <div>
            <label htmlFor={`${idPraefix}-jahr`}>Jahr</label>
            <input
              id={`${idPraefix}-jahr`}
              type="number"
              inputMode="numeric"
              placeholder="1950"
              min={1800}
              max={2100}
              value={werte.jahr}
              onChange={(event) => setzen("jahr", event.target.value)}
            />
          </div>
          <div>
            <label htmlFor={`${idPraefix}-monat`}>Monat</label>
            <select
              id={`${idPraefix}-monat`}
              value={werte.monat}
              onChange={(event) => setzen("monat", event.target.value)}
            >
              <option value="">Kein Monat</option>
              {monatsNamen.map((name, stelle) => (
                <option key={name} value={String(stelle + 1)}>
                  {name}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor={`${idPraefix}-tag`}>Tag</label>
            <input
              id={`${idPraefix}-tag`}
              type="number"
              inputMode="numeric"
              placeholder="12"
              min={1}
              max={31}
              value={werte.tag}
              onChange={(event) => setzen("tag", event.target.value)}
            />
          </div>
        </div>
        <label
          htmlFor={`${idPraefix}-unsicher`}
          className="auftritt-unsicher-wahl"
        >
          <input
            id={`${idPraefix}-unsicher`}
            type="checkbox"
            checked={werte.unsicher}
            onChange={(event) =>
              setWerte((bisher) => ({
                ...bisher,
                unsicher: event.target.checked,
              }))
            }
          />
          Datum unsicher
        </label>
        <p className="feld-hinweis">
          „Datum unsicher“ kennzeichnet überlieferte Angaben, die nur ungefähr
          stimmen, etwa „um 1950“.
        </p>
        {datumFehler && (
          <p role="alert" className="feld-fehler">
            {datumFehler}
          </p>
        )}
      </fieldset>
      <label htmlFor={`${idPraefix}-ort`}>Ort (optional)</label>
      <input
        id={`${idPraefix}-ort`}
        type="text"
        maxLength={200}
        value={werte.venue}
        onChange={(event) => setzen("venue", event.target.value)}
      />
      <label htmlFor={`${idPraefix}-zeit`}>Uhrzeit (optional)</label>
      <input
        id={`${idPraefix}-zeit`}
        type="text"
        maxLength={10}
        placeholder="19:30"
        value={werte.zeit}
        onChange={(event) => setzen("zeit", event.target.value)}
        aria-invalid={zeitFehler ? true : undefined}
        aria-describedby={`${idPraefix}-zeit-hinweis`}
      />
      {zeitFehler && (
        <p id={`${idPraefix}-zeit-fehler`} role="alert" className="feld-fehler">
          {zeitFehler}
        </p>
      )}
      <p id={`${idPraefix}-zeit-hinweis`} className="feld-hinweis">
        Im Format HH:MM, zum Beispiel 19:30.
      </p>
      <label htmlFor={`${idPraefix}-hinweise`}>Hinweise (optional)</label>
      <textarea
        id={`${idPraefix}-hinweise`}
        rows={4}
        maxLength={2000}
        value={werte.hinweise}
        onChange={(event) => setzen("hinweise", event.target.value)}
      />
      <p className="feld-hinweis">
        Praktische Notizen, die Mitglieder an diesem Auftritt sehen.
      </p>
      <label htmlFor={`${idPraefix}-quelle`}>Quelle (optional)</label>
      <input
        id={`${idPraefix}-quelle`}
        type="text"
        maxLength={500}
        value={werte.quelle}
        onChange={(event) => setzen("quelle", event.target.value)}
      />
      <p className="feld-hinweis">
        Woher die Angaben stammen, etwa Programmheft oder Protokoll.
      </p>
      {hinweis && (
        <output aria-live="polite" className="feld-fehler">
          {hinweis}
        </output>
      )}
      <div className="auth-aktionen">
        <button type="submit" disabled={busy}>
          {busy ? "Wird gespeichert …" : absendenText}
        </button>
        {onAbbrechen && (
          <button
            type="button"
            className="knopf-leise"
            disabled={busy}
            onClick={onAbbrechen}
          >
            Abbrechen
          </button>
        )}
      </div>
    </form>
  );
}
