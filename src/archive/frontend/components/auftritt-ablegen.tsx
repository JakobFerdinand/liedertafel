"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useState } from "react";
import { arten, monatsNamen } from "@/components/auftritt-formular";
import { LiedWahl } from "@/components/lied-wahl";
import {
  AuftrittEntwurfFehler,
  ladeAuftrittEntwurf,
} from "@/lib/auftritt-entwuerfe";
import { ausGespeichert } from "@/lib/auftritt-entwurf/gespeichert";
import {
  type DateiArt,
  type DateiEintrag,
  type Feld,
  kannSpeichern,
  type ProgrammZeile,
  pruefeWerte,
  type Zustand,
} from "@/lib/auftritt-entwurf/reducer";
import { useAuftrittEntwurf } from "@/lib/auftritt-entwurf/use-auftritt-entwurf";
import { liedUrl } from "@/lib/events";
import { groesseText } from "@/lib/uebertragung";

const dateiArten: [DateiArt, string][] = [
  ["programm", "Programmzettel"],
  ["aufnahme", "Aufnahme"],
  ["sonstiges", "Sonstiges Dokument"],
];

function lesenText(datei: DateiEintrag): string | null {
  switch (datei.lesen.status) {
    case "keine":
      return null;
    case "liest":
      return "Wird gelesen …";
    case "gelesen":
      return "Gelesen";
    case "fehlgeschlagen":
    case "nicht-verfuegbar":
      return datei.lesen.meldung;
  }
}

function uploadText(datei: DateiEintrag): string {
  switch (datei.upload.status) {
    case "wartend":
      return "Bereit";
    case "laedt":
      return "Wird hochgeladen …";
    case "hochgeladen":
      return "Hochgeladen";
    case "fehlgeschlagen":
      return `Fehlgeschlagen: ${datei.upload.meldung}`;
  }
}

/** Hinweis hinter einem Feld, das ein Lesen gefüllt hat und das noch niemand angefasst hat. */
function VorschlagMarke({ zustand, feld }: { zustand: Zustand; feld: Feld }) {
  const sicherheit = zustand.vorschlaege[feld];
  if (!sicherheit) return null;
  return (
    <span className="feld-hinweis" data-vorschlag={sicherheit}>
      {sicherheit === "unsicher" ? "Vorschlag, unsicher" : "Vorschlag"}
    </span>
  );
}

/**
 * Was zum Speichern noch fehlt, in Worten: ein ausgegrauter Knopf ohne Grund
 * hilft niemandem.
 */
function wasFehlt(zustand: Zustand): string[] {
  const gruende: string[] = [];
  const pruefung = pruefeWerte(zustand.werte);
  if (pruefung.titel) gruende.push("ein Titel");
  if (pruefung.datum) gruende.push("ein gültiges Datum");
  const ohneLied = zustand.programm.filter((z) => z.lied === null).length;
  if (ohneLied > 0) {
    gruende.push(
      ohneLied === 1
        ? "ein Lied für den offenen Programmpunkt"
        : `ein Lied für die ${ohneLied} offenen Programmpunkte`,
    );
  }
  if (zustand.dateien.some((d) => d.lesen.status === "liest"))
    gruende.push("das Ende des Lesens");
  const fehlend = zustand.dateien.filter((d) => d.inhalt === "fehlt");
  if (fehlend.length > 0)
    gruende.push(
      `${fehlend.map((d) => d.name).join(", ")} noch einmal abgelegt`,
    );
  return gruende;
}

/**
 * "Neuer Auftritt" (#131): einen Programmzettel (Scan oder Foto) oder ein
 * Konzertvideo ablegen, das Formular füllt sich, ein Klick auf "Speichern" legt
 * den Auftritt mit Programm und Dateien an. Ohne Datei geht es über den
 * kleinen Link zum Formular von Hand.
 */
export function AuftrittAblegen({
  entwurfId,
  onOhneDatei,
  onVerworfen,
}: {
  entwurfId?: string | null;
  onOhneDatei: () => void;
  onVerworfen: () => void;
}) {
  const [fortsetzen, setFortsetzen] = useState<
    | { status: "laedt" }
    | { status: "bereit"; zustand: Zustand }
    | { status: "fehler"; meldung: string }
    | null
  >(entwurfId ? { status: "laedt" } : null);

  useEffect(() => {
    if (!entwurfId) {
      setFortsetzen(null);
      return;
    }
    const steuerung = new AbortController();
    setFortsetzen({ status: "laedt" });
    ladeAuftrittEntwurf(entwurfId, steuerung.signal).then(
      (roh) => {
        const zustand = ausGespeichert(roh, entwurfId);
        setFortsetzen(
          zustand
            ? { status: "bereit", zustand }
            : {
                status: "fehler",
                meldung: "Der angefangene Eintrag ist nicht lesbar.",
              },
        );
      },
      (ursache) => {
        if (steuerung.signal.aborted) return;
        setFortsetzen({
          status: "fehler",
          meldung:
            ursache instanceof AuftrittEntwurfFehler
              ? ursache.message
              : "Der Eintrag konnte nicht geladen werden.",
        });
      },
    );
    return () => steuerung.abort();
  }, [entwurfId]);

  if (fortsetzen?.status === "laedt") {
    return (
      <p aria-live="polite" className="feld-hinweis">
        Angefangener Auftritt wird geladen …
      </p>
    );
  }
  return (
    <>
      {fortsetzen?.status === "fehler" && (
        <p role="alert" className="feld-fehler">
          {fortsetzen.meldung}
        </p>
      )}
      <DateienAblegen
        key={entwurfId ?? "neu"}
        wiederhergestellt={
          fortsetzen?.status === "bereit" ? fortsetzen.zustand : undefined
        }
        onOhneDatei={onOhneDatei}
        onVerworfen={onVerworfen}
      />
    </>
  );
}

function ProgrammZeileAnsicht({
  zeile,
  index,
  anzahl,
  bearbeitbar,
  aktionen,
}: {
  zeile: ProgrammZeile;
  index: number;
  anzahl: number;
  bearbeitbar: boolean;
  aktionen: ReturnType<typeof useAuftrittEntwurf>;
}) {
  const [waehlen, setWaehlen] = useState(false);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);
  const idPraefix = useId();

  async function neuesLied() {
    setBusy(true);
    setFehler("");
    const meldung = await aktionen.neuesLiedAnlegen(zeile.id);
    if (meldung) setFehler(meldung);
    setBusy(false);
  }

  return (
    <li
      data-lied={zeile.lied ? (zeile.sicher ? "sicher" : "unsicher") : "offen"}
    >
      <p className="lied-ablegen-name">
        {index + 1}. {zeile.text}
      </p>
      {zeile.lied ? (
        <p className="feld-hinweis">
          {zeile.sicher ? "Lied: " : "Vorschlag, unsicher: "}
          <Link href={liedUrl(zeile.lied.id)} target="_blank" rel="noreferrer">
            {zeile.lied.titel}
          </Link>
        </p>
      ) : (
        <p className="feld-hinweis">Noch kein Lied zugeordnet.</p>
      )}
      {bearbeitbar && (
        <div className="auth-aktionen">
          {zeile.lied && !zeile.sicher && (
            <button
              type="button"
              onClick={() => aktionen.zeileAnnehmen(zeile.id)}
            >
              {zeile.text} übernehmen
            </button>
          )}
          {zeile.kandidaten
            .filter((kandidat) => kandidat.id !== zeile.lied?.id)
            .map((kandidat) => (
              <button
                key={kandidat.id}
                type="button"
                className="knopf-leise-rahmen"
                onClick={() => aktionen.zeileWaehlen(zeile.id, kandidat)}
              >
                {zeile.text}: „{kandidat.titel}“ wählen
              </button>
            ))}
          {!zeile.lied && (
            <button
              type="button"
              className="knopf-leise-rahmen"
              disabled={busy}
              onClick={() => void neuesLied()}
            >
              {zeile.text} als neues Lied anlegen
            </button>
          )}
          <button
            type="button"
            className="knopf-leise"
            aria-expanded={waehlen}
            onClick={() => setWaehlen(!waehlen)}
          >
            {zeile.text}: anderes Lied suchen
          </button>
          <button
            type="button"
            className="knopf-leise"
            disabled={index === 0}
            onClick={() => aktionen.zeileVerschieben(zeile.id, -1)}
          >
            {zeile.text} nach oben
          </button>
          <button
            type="button"
            className="knopf-leise"
            disabled={index === anzahl - 1}
            onClick={() => aktionen.zeileVerschieben(zeile.id, 1)}
          >
            {zeile.text} nach unten
          </button>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => aktionen.zeileEntfernen(zeile.id)}
          >
            {zeile.text} entfernen
          </button>
        </div>
      )}
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {bearbeitbar && waehlen && (
        <LiedWahl
          kennung={`${idPraefix}-suche`}
          beschriftung={`Lied für ${zeile.text} suchen`}
          onGewaehlt={(id, titel) => {
            aktionen.zeileWaehlen(zeile.id, { id, titel });
            setWaehlen(false);
          }}
        />
      )}
    </li>
  );
}

function DateienAblegen({
  wiederhergestellt,
  onOhneDatei,
  onVerworfen,
}: {
  wiederhergestellt?: Zustand;
  onOhneDatei: () => void;
  onVerworfen: () => void;
}) {
  const router = useRouter();
  const idPraefix = useId();
  const [darueber, setDarueber] = useState(false);
  const aktionen = useAuftrittEntwurf(wiederhergestellt);
  const { zustand } = aktionen;

  const gespeichert = zustand.phase === "gespeichert";
  const auftrittId = zustand.auftrittId;
  useEffect(() => {
    if (gespeichert && auftrittId) {
      router.push(`/auftritt/?id=${encodeURIComponent(auftrittId)}`);
    }
  }, [gespeichert, auftrittId, router]);

  const bearbeitbar = zustand.phase === "sammeln";
  const pruefung = pruefeWerte(zustand.werte);
  const fehlt = wasFehlt(zustand);
  const hatDateien = zustand.dateien.length > 0;
  const kannWiederholen =
    zustand.phase === "speichern" &&
    zustand.fehler !== null &&
    zustand.dateien.some((d) => d.upload.status !== "hochgeladen");

  return (
    <div className="lied-ablegen">
      {(bearbeitbar || zustand.phase === "speichern") && (
        <label
          className="lied-ablegen-zone"
          data-darueber={darueber ? "ja" : undefined}
          onDragOver={(event) => {
            event.preventDefault();
            setDarueber(true);
          }}
          onDragLeave={() => setDarueber(false)}
          onDrop={(event) => {
            event.preventDefault();
            setDarueber(false);
            aktionen.ablegen(Array.from(event.dataTransfer.files));
          }}
        >
          <span>
            Programmzettel oder Konzertvideo hier ablegen oder auswählen
          </span>
          <span className="feld-hinweis">
            Scan oder Foto des Programms (PDF, JPEG, PNG, WebP), Video oder
            Tonaufnahme des Konzerts.
          </span>
          <input
            type="file"
            multiple
            aria-label="Dateien für den Auftritt auswählen"
            onChange={(event) => {
              aktionen.ablegen(Array.from(event.target.files ?? []));
              event.target.value = "";
            }}
          />
        </label>
      )}

      {hatDateien && (
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void aktionen.speichern();
          }}
          noValidate
        >
          {zustand.ki.status === "nicht-verfuegbar" && (
            <p className="feld-hinweis" data-ki="nicht-verfuegbar">
              Die KI steht nicht zur Verfügung: {zustand.ki.meldung} Bitte die
              Felder von Hand ausfüllen; Speichern geht trotzdem.
            </p>
          )}
          <label htmlFor={`${idPraefix}-typ`}>Auftrittstyp</label>
          <select
            id={`${idPraefix}-typ`}
            value={zustand.werte.kind}
            disabled={!bearbeitbar}
            onChange={(event) =>
              aktionen.feldAendern("kind", event.target.value)
            }
          >
            {arten.map(([wert, name]) => (
              <option key={wert} value={wert}>
                {name}
              </option>
            ))}
          </select>
          <VorschlagMarke zustand={zustand} feld="kind" />
          <label htmlFor={`${idPraefix}-titel`}>Titel</label>
          <input
            id={`${idPraefix}-titel`}
            type="text"
            maxLength={200}
            value={zustand.werte.title}
            disabled={!bearbeitbar}
            onChange={(event) =>
              aktionen.feldAendern("title", event.target.value)
            }
          />
          <VorschlagMarke zustand={zustand} feld="title" />
          <fieldset className="auftritt-datum-felder">
            <legend>Datum</legend>
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
                  value={zustand.werte.jahr}
                  disabled={!bearbeitbar}
                  onChange={(event) =>
                    aktionen.feldAendern("jahr", event.target.value)
                  }
                />
                <VorschlagMarke zustand={zustand} feld="jahr" />
              </div>
              <div>
                <label htmlFor={`${idPraefix}-monat`}>Monat</label>
                <select
                  id={`${idPraefix}-monat`}
                  value={zustand.werte.monat}
                  disabled={!bearbeitbar}
                  onChange={(event) =>
                    aktionen.feldAendern("monat", event.target.value)
                  }
                >
                  <option value="">Kein Monat</option>
                  {monatsNamen.map((name, stelle) => (
                    <option key={name} value={String(stelle + 1)}>
                      {name}
                    </option>
                  ))}
                </select>
                <VorschlagMarke zustand={zustand} feld="monat" />
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
                  value={zustand.werte.tag}
                  disabled={!bearbeitbar}
                  onChange={(event) =>
                    aktionen.feldAendern("tag", event.target.value)
                  }
                />
                <VorschlagMarke zustand={zustand} feld="tag" />
              </div>
            </div>
            <label
              htmlFor={`${idPraefix}-unsicher`}
              className="auftritt-unsicher-wahl"
            >
              <input
                id={`${idPraefix}-unsicher`}
                type="checkbox"
                checked={zustand.werte.unsicher}
                disabled={!bearbeitbar}
                onChange={(event) =>
                  aktionen.unsicherAendern(event.target.checked)
                }
              />
              Datum unsicher
            </label>
            <VorschlagMarke zustand={zustand} feld="unsicher" />
            {pruefung.datum && (
              <p role="alert" className="feld-fehler">
                {pruefung.datum}
              </p>
            )}
          </fieldset>
          <label htmlFor={`${idPraefix}-ort`}>Ort (optional)</label>
          <input
            id={`${idPraefix}-ort`}
            type="text"
            maxLength={200}
            value={zustand.werte.venue}
            disabled={!bearbeitbar}
            onChange={(event) =>
              aktionen.feldAendern("venue", event.target.value)
            }
          />
          <VorschlagMarke zustand={zustand} feld="venue" />

          <section aria-labelledby={`${idPraefix}-programm`}>
            <h3 id={`${idPraefix}-programm`}>Programm</h3>
            {zustand.programm.length === 0 ? (
              <p className="feld-hinweis">
                Noch keine Programmpunkte. Beim Lesen eines Programmzettels
                erscheinen sie hier, sonst lassen sich Lieder von Hand
                hinzufügen.
              </p>
            ) : (
              <ol className="lied-ablegen-dateien">
                {zustand.programm.map((zeile, index) => (
                  <ProgrammZeileAnsicht
                    key={zeile.id}
                    zeile={zeile}
                    index={index}
                    anzahl={zustand.programm.length}
                    bearbeitbar={bearbeitbar}
                    aktionen={aktionen}
                  />
                ))}
              </ol>
            )}
            {bearbeitbar && (
              <LiedWahl
                kennung={`${idPraefix}-lied-hinzu`}
                beschriftung="Lied zum Programm hinzufügen"
                onGewaehlt={(id, titel) =>
                  aktionen.zeileHinzufuegen({ id, titel })
                }
              />
            )}
          </section>

          <ul className="lied-ablegen-dateien">
            {zustand.dateien.map((datei) => (
              <li key={datei.id} data-upload={datei.upload.status}>
                <p className="lied-ablegen-name">
                  {datei.name}{" "}
                  <span className="feld-hinweis">
                    ({groesseText(datei.sizeBytes)})
                  </span>
                </p>
                <label htmlFor={`${idPraefix}-${datei.id}-art`}>
                  Art von {datei.name}
                </label>
                <select
                  id={`${idPraefix}-${datei.id}-art`}
                  value={datei.art}
                  disabled={!bearbeitbar}
                  onChange={(event) =>
                    aktionen.dateiArtAendern(
                      datei.id,
                      event.target.value as DateiArt,
                    )
                  }
                >
                  {dateiArten.map(([wert, text]) => (
                    <option key={wert} value={wert}>
                      {text}
                    </option>
                  ))}
                </select>
                {datei.inhalt === "fehlt" &&
                  datei.upload.status !== "hochgeladen" && (
                    <p className="feld-hinweis">
                      Die Datei liegt nicht mehr im Browser. Bitte {datei.name}{" "}
                      erneut ablegen oder auswählen, um den Auftritt zu
                      speichern.
                    </p>
                  )}
                {datei.upload.status === "laedt" && datei.upload.gesamt > 0 && (
                  <div className="material-fortschritt" aria-hidden="true">
                    <span
                      style={{
                        width: `${Math.round((datei.upload.bytes / datei.upload.gesamt) * 100)}%`,
                      }}
                    />
                  </div>
                )}
                {lesenText(datei) && (
                  <p
                    className={
                      datei.lesen.status === "fehlgeschlagen"
                        ? "feld-fehler"
                        : "feld-hinweis"
                    }
                    role={
                      datei.lesen.status === "fehlgeschlagen"
                        ? "alert"
                        : undefined
                    }
                  >
                    {lesenText(datei)}
                  </p>
                )}
                {datei.art === "programm" &&
                  bearbeitbar &&
                  datei.inhalt === "vorhanden" &&
                  (datei.lesen.status === "fehlgeschlagen" ||
                    datei.lesen.status === "keine") && (
                    <button
                      type="button"
                      className="knopf-leise"
                      onClick={() => void aktionen.lesen(datei.id)}
                    >
                      {datei.name} lesen
                    </button>
                  )}
                {zustand.phase !== "sammeln" && (
                  <p
                    className={
                      datei.upload.status === "fehlgeschlagen"
                        ? "feld-fehler"
                        : "feld-hinweis"
                    }
                    role={
                      datei.upload.status === "fehlgeschlagen"
                        ? "alert"
                        : undefined
                    }
                  >
                    {uploadText(datei)}
                  </p>
                )}
                {bearbeitbar && (
                  <button
                    type="button"
                    className="knopf-leise"
                    onClick={() => aktionen.dateiEntfernen(datei.id)}
                  >
                    {datei.name} entfernen
                  </button>
                )}
              </li>
            ))}
          </ul>

          {zustand.phase === "speichern" && zustand.fehler && (
            <p role="alert" className="feld-fehler">
              {zustand.fehler}
            </p>
          )}
          {bearbeitbar && fehlt.length > 0 && (
            <p className="feld-hinweis" data-fehlt="ja">
              Zum Speichern fehlt noch: {fehlt.join(", ")}.
            </p>
          )}
          {auftrittId && zustand.phase !== "sammeln" && (
            <p className="feld-hinweis">
              Der Auftritt ist angelegt.{" "}
              <Link href={`/auftritt/?id=${encodeURIComponent(auftrittId)}`}>
                Zum Auftritt
              </Link>
            </p>
          )}
          <div className="auth-aktionen">
            <button
              type="submit"
              disabled={
                bearbeitbar ? !kannSpeichern(zustand) : !kannWiederholen
              }
            >
              {zustand.phase === "speichern" && !zustand.fehler
                ? "Wird gespeichert …"
                : zustand.phase === "speichern"
                  ? "Erneut versuchen"
                  : "Speichern"}
            </button>
            {zustand.phase !== "gespeichert" && !auftrittId && (
              <button
                type="button"
                className="knopf-leise"
                onClick={() => void aktionen.verwerfen().then(onVerworfen)}
              >
                Entwurf verwerfen
              </button>
            )}
          </div>
        </form>
      )}

      {!hatDateien && (
        <p className="feld-hinweis">
          <button type="button" className="knopf-leise" onClick={onOhneDatei}>
            ohne Datei anlegen
          </button>
        </p>
      )}
    </div>
  );
}
