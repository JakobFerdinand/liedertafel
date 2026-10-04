"use client";

import { useEffect, useRef, useState } from "react";
import {
  type AssetAccessResponse,
  type DateiStandWechsel,
  type DateiVerlauf,
  fetchDateiStandAccess,
  fetchDateiVerlauf,
  groesseText,
  MaterialFehler,
  problemTitel,
  setzeAktuellenDateiStand,
  uebertrageDatei,
} from "@/lib/assets";
import { publishedAtText } from "@/lib/events";

type NotenVerlaufProps = {
  assetId: string;
  stimme: string;
  /** Der aktuelle Dateistand hat gewechselt (Korrektur oder Rückgriff). */
  onGeaendert: () => void;
};

type LadeZustand = "laden" | "bereit" | "fehler";

type Uebertragung =
  | { schritt: "uebertragen"; fortschritt: number }
  | { schritt: "geprueft" };

function standName(nummer: number | null): string {
  return nummer === null ? "ein entfernter Dateistand" : `Dateistand ${nummer}`;
}

function wechselText(wechsel: DateiStandWechsel): string {
  const wer = wechsel.changedBy ?? "Unbekannt";
  const zuvor =
    wechsel.previousRevisionNumber === null
      ? ""
      : ` (zuvor Dateistand ${wechsel.previousRevisionNumber})`;
  return wechsel.kind === "restore"
    ? `${wer} hat ${standName(wechsel.revisionNumber)} wieder als aktuell festgelegt${zuvor}.`
    : `${wer} hat ${standName(wechsel.revisionNumber)} hochgeladen${zuvor}.`;
}

/**
 * Redaktionsverlauf eines Notenmaterials (ARC-033): korrigierte Datei
 * hochladen, frühere Dateistände ansehen und einen davon wieder als aktuell
 * festlegen. Mitglieder sehen diesen Bereich nie; sie erhalten immer den
 * aktuellen Dateistand derselben Fassung.
 */
export function NotenVerlauf({
  assetId,
  stimme,
  onGeaendert,
}: NotenVerlaufProps) {
  const eingabeRef = useRef<HTMLInputElement>(null);
  const [ladeZustand, setLadeZustand] = useState<LadeZustand>("laden");
  const [verlauf, setVerlauf] = useState<DateiVerlauf | null>(null);
  const [ladeVersuch, setLadeVersuch] = useState(0);
  const [uebertragung, setUebertragung] = useState<Uebertragung | null>(null);
  const [wechselBusy, setWechselBusy] = useState<string | null>(null);
  const [erfolg, setErfolg] = useState("");
  const [fehler, setFehler] = useState("");
  const [zugriffe, setZugriffe] = useState<Record<string, AssetAccessResponse>>(
    {},
  );
  const [zugriffBusy, setZugriffBusy] = useState<string | null>(null);

  // biome-ignore lint/correctness/useExhaustiveDependencies: ladeVersuch löst das erneute Laden aus.
  useEffect(() => {
    const abbruch = new AbortController();
    setLadeZustand("laden");
    fetchDateiVerlauf(assetId, abbruch.signal)
      .then((antwort) => {
        setVerlauf(antwort);
        setLadeZustand("bereit");
      })
      .catch(() => {
        if (!abbruch.signal.aborted) setLadeZustand("fehler");
      });
    return () => abbruch.abort();
  }, [assetId, ladeVersuch]);

  const beschaeftigt = uebertragung !== null || wechselBusy !== null;

  async function korrekturHochladen(datei: File) {
    setErfolg("");
    setFehler("");
    setUebertragung({ schritt: "uebertragen", fortschritt: 0 });
    try {
      const revision = await uebertrageDatei(
        assetId,
        datei,
        "application/pdf",
        (schritt) => {
          if (schritt === "geprueft") setUebertragung({ schritt });
        },
        {
          onFortschritt: (uebertragenBytes, gesamtBytes) =>
            setUebertragung({
              schritt: "uebertragen",
              fortschritt:
                gesamtBytes > 0 ? (uebertragenBytes / gesamtBytes) * 100 : 100,
            }),
        },
      );
      setErfolg(
        `Dateistand ${revision.revisionNumber} ist jetzt aktuell. Mitglieder erhalten ab sofort diese Datei; der frühere Dateistand bleibt erhalten.`,
      );
      setZugriffe({});
      setLadeVersuch((versuch) => versuch + 1);
      onGeaendert();
    } catch (ursache) {
      setFehler(
        ursache instanceof MaterialFehler
          ? ursache.message
          : await problemTitel(
              ursache,
              "Die korrigierten Noten konnten nicht hochgeladen werden. Der bisherige Dateistand bleibt aktuell.",
            ),
      );
    } finally {
      setUebertragung(null);
    }
  }

  function dateiGewaehlt(event: React.ChangeEvent<HTMLInputElement>) {
    const datei = event.target.files?.[0];
    event.target.value = "";
    if (datei) void korrekturHochladen(datei);
  }

  async function alsAktuellFestlegen(revisionId: string, nummer: number) {
    if (!verlauf) return;
    setErfolg("");
    setFehler("");
    setWechselBusy(revisionId);
    try {
      setVerlauf(
        await setzeAktuellenDateiStand(
          assetId,
          revisionId,
          verlauf.currentRevisionId,
        ),
      );
      setErfolg(
        `Dateistand ${nummer} ist wieder aktuell. Mitglieder erhalten ab sofort diese Datei; alle Dateistände bleiben erhalten.`,
      );
      onGeaendert();
    } catch (ursache) {
      const konflikt = ursache instanceof Response && ursache.status === 409;
      setFehler(
        konflikt
          ? "Die Noten wurden zwischenzeitlich geändert. Der Verlauf wurde neu geladen — bitte prüfen und erneut wählen."
          : await problemTitel(
              ursache,
              "Der Dateistand konnte nicht festgelegt werden. Bitte erneut versuchen.",
            ),
      );
      if (konflikt) setLadeVersuch((versuch) => versuch + 1);
    } finally {
      setWechselBusy(null);
    }
  }

  async function dateiAbrufen(revisionId: string) {
    setFehler("");
    setZugriffBusy(revisionId);
    try {
      const zugriff = await fetchDateiStandAccess(assetId, revisionId);
      setZugriffe((vorher) => ({ ...vorher, [revisionId]: zugriff }));
    } catch (ursache) {
      setFehler(
        ursache instanceof Response && ursache.status === 404
          ? "Dieser Dateistand ist nicht mehr vorhanden."
          : "Der Dateistand konnte nicht abgerufen werden. Bitte erneut versuchen.",
      );
    } finally {
      setZugriffBusy(null);
    }
  }

  return (
    <section
      className="noten-verlauf"
      aria-label={`Dateistände · ${stimme}`}
      aria-busy={ladeZustand === "laden" || beschaeftigt}
    >
      <h5>Dateistände</h5>
      <p className="noten-info">
        Eine Korrektur ersetzt nur die Datei. Fassung und Bearbeitung bleiben
        dieselben, und frühere Dateistände bleiben für die Redaktion erhalten,
        bis sie ausdrücklich entfernt werden.
      </p>
      <div className="noten-aktionen">
        <button
          type="button"
          onClick={() => eingabeRef.current?.click()}
          disabled={beschaeftigt}
        >
          Korrigierte Noten hochladen
        </button>
        <input
          ref={eingabeRef}
          className="visually-hidden"
          type="file"
          accept="application/pdf,.pdf"
          onChange={dateiGewaehlt}
          disabled={beschaeftigt}
          tabIndex={-1}
          aria-hidden="true"
        />
      </div>
      {uebertragung && (
        <output aria-live="polite" className="noten-info">
          {uebertragung.schritt === "uebertragen"
            ? `Korrektur wird übertragen … ${Math.round(uebertragung.fortschritt)} %`
            : "Korrektur wird geprüft …"}
        </output>
      )}
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}
        </p>
      )}
      {ladeZustand === "laden" && !verlauf && (
        <p className="noten-info">Dateistände werden geladen …</p>
      )}
      {ladeZustand === "fehler" && (
        <div className="noten-aktionen">
          <p role="alert" className="feld-fehler">
            Die Dateistände konnten nicht geladen werden.
          </p>
          <button
            type="button"
            className="knopf-leise"
            onClick={() => setLadeVersuch((versuch) => versuch + 1)}
          >
            Erneut laden
          </button>
        </div>
      )}
      {verlauf && (
        <>
          <ol className="noten-verlauf-liste">
            {verlauf.revisions.map((stand) => {
              const zugriff = zugriffe[stand.revisionId] ?? null;
              return (
                <li
                  key={stand.revisionId}
                  className="noten-verlauf-stand"
                  aria-current={stand.isCurrent ? "true" : undefined}
                >
                  <p className="noten-verlauf-titel">
                    Dateistand {stand.revisionNumber}
                    {stand.isCurrent && (
                      <span className="noten-verlauf-aktuell"> · aktuell</span>
                    )}
                  </p>
                  <p className="noten-info">
                    {stand.fileName ?? "Dateiname nicht erfasst"} ·{" "}
                    {groesseText(stand.sizeBytes)} · hochgeladen am{" "}
                    {publishedAtText(stand.createdAt)} von{" "}
                    {stand.createdBy ?? "unbekannt"}
                  </p>
                  <div className="noten-aktionen">
                    {zugriff ? (
                      <>
                        <a
                          className="noten-laden"
                          href={zugriff.viewUrl}
                          target="_blank"
                          rel="noreferrer"
                          aria-label={`Dateistand ${stand.revisionNumber} öffnen`}
                        >
                          Öffnen
                        </a>
                        <a
                          className="noten-laden"
                          href={zugriff.downloadUrl}
                          download
                          aria-label={`Dateistand ${stand.revisionNumber} herunterladen`}
                        >
                          Herunterladen
                        </a>
                      </>
                    ) : (
                      <button
                        type="button"
                        className="knopf-leise"
                        onClick={() => void dateiAbrufen(stand.revisionId)}
                        disabled={zugriffBusy === stand.revisionId}
                        aria-label={`Dateistand ${stand.revisionNumber} ansehen`}
                      >
                        {zugriffBusy === stand.revisionId
                          ? "Wird vorbereitet …"
                          : "Ansehen"}
                      </button>
                    )}
                    {!stand.isCurrent && (
                      <button
                        type="button"
                        className="knopf-leise"
                        onClick={() =>
                          void alsAktuellFestlegen(
                            stand.revisionId,
                            stand.revisionNumber,
                          )
                        }
                        disabled={beschaeftigt}
                        aria-label={`Dateistand ${stand.revisionNumber} als aktuell festlegen`}
                      >
                        {wechselBusy === stand.revisionId
                          ? "Wird festgelegt …"
                          : "Als aktuell festlegen"}
                      </button>
                    )}
                  </div>
                </li>
              );
            })}
          </ol>
          {verlauf.revisions.length < 2 && (
            <p className="noten-info">
              Es gibt noch keinen früheren Dateistand.
            </p>
          )}
          {verlauf.changes.length > 0 && (
            <details className="noten-verlauf-wechsel">
              <summary>Verlauf der Änderungen</summary>
              <ol>
                {verlauf.changes.map((wechsel) => (
                  <li key={`${wechsel.changedAt}-${wechsel.revisionId}`}>
                    <span className="noten-info">
                      {publishedAtText(wechsel.changedAt)}
                    </span>{" "}
                    {wechselText(wechsel)}
                  </li>
                ))}
              </ol>
            </details>
          )}
        </>
      )}
    </section>
  );
}
