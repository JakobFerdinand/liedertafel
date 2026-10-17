"use client";

// ARC-025 / #171: Dokumente und Fotografien am Auftritt sind Dateien des
// Auftritts — Lesesaal (Fotografien mit Bildunterschrift und Vorlesetext,
// Dokumente mit Öffnen/Herunterladen) und die Redaktions-Werkbank
// (Upload-Sitzung → Übertragung zum Ticket → Abschluss, Fortschritt je Zeile,
// Erneut-versuchen, Fortsetzen unterbrochener Übertragungen aus dem lokalen
// Speicher, Ersetzen und Löschen über den Papierkorb).

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  AbbruchFehler,
  cancelUploadSession,
  entferneUploadSitzung,
  groesseText,
  liesUploadSitzung,
  MaterialFehler,
  type MaterialSchritt,
  problemTitel,
  restlaufzeitMs,
} from "@/lib/assets";
import {
  aendereDateiBeschreibung,
  auftrittUploadSchluessel,
  type DateiZugriff,
  fetchDateiZugriff,
  ladeAuftrittDateiHoch,
  loescheDatei,
  setzeAuftrittUebertragungFort,
  unterbrocheneAuftrittUebertragungen,
} from "@/lib/dateien";
import {
  type AuftrittDetails,
  contentTypeFuerDokument,
  type Dokument,
  dokumentArtName,
} from "@/lib/events";

/** Vorlauf vor dem Ticketablauf, zu dem still neue Tickets angefordert werden. */
const ErneuerungsVorlaufMs = 60_000;

/** Nachlauf für einen stillen Erneuerungsversuch nach einem Fehlschlag. */
const ErneuerungsWiederholungMs = 30_000;

/** Wiederholbare Meldung für vorübergehende Ladefehler. */
const LadeErsatz =
  "Das Material konnte nicht geladen werden. Bitte erneut versuchen.";

const startMeldung =
  "Das Hochladen konnte nicht gestartet werden. Bitte erneut versuchen.";

const DokumentPlatz = "dokument-";

// Tickets werden pro Material geholt: entfernte Dateien (404) erklären
// sich selbst, abgelaufene Anmeldungen weisen aufs Neuladen, Vorübergehendes
// bietet den erneuten Versuch.
function zugriffsFehler(ursache: unknown, ersatz: string): string {
  const status = ursache instanceof Response ? ursache.status : 0;
  if (status === 404) return "Das Material ist nicht mehr verfügbar.";
  if (status === 401) {
    return "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.";
  }
  return ersatz;
}

function typText(contentType: string): string {
  switch (contentType.toLowerCase()) {
    case "application/pdf":
      return "PDF";
    case "image/jpeg":
      return "JPEG";
    case "image/png":
      return "PNG";
    case "image/webp":
      return "WEBP";
    default:
      return contentType;
  }
}

/** Sichtbare Zeile: die Beschreibung oder der ehrliche Art-Ersatz. */
function beschriftungVon(dokument: Dokument): string {
  return dokument.description?.trim() || dokumentArtName(dokument.art);
}

/** Vorlesetext des Bildes: die Beschreibung oder der menschliche Ersatz. */
function altTextVon(dokument: Dokument): string {
  return dokument.description?.trim() || "Fotografie zum Auftritt";
}

// Die Ticket-Abfrage je Material: einmal je Eintrag, still erneuert, bevor
// das Ticket abläuft (Rezeptur des Audio-Spielers), mit eigenen
// Fehlerzuständen pro Eintrag.
function useTicketzugriff(dateiId: string) {
  const [zugriff, setZugriff] = useState<DateiZugriff | null>(null);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(true);
  const timerRef = useRef<number | undefined>(undefined);

  const laden = useCallback(
    async (signal?: AbortSignal, still = false) => {
      try {
        const neu = await fetchDateiZugriff(dateiId, signal);
        if (signal?.aborted) return;
        setZugriff(neu);
        setFehler("");
      } catch (ursache) {
        if (signal?.aborted) return;
        if (still) {
          // Stiller Wiederholungsversuch: das alte Ticket kann weiterlaufen.
          window.clearTimeout(timerRef.current);
          timerRef.current = window.setTimeout(() => {
            void laden(undefined, true);
          }, ErneuerungsWiederholungMs);
          return;
        }
        setFehler(zugriffsFehler(ursache, LadeErsatz));
      } finally {
        if (!signal?.aborted) setBusy(false);
      }
    },
    [dateiId],
  );

  // Erneuert das Ticket kurz vor Ablauf; der Schlüssel auf `zugriff` plant
  // nach jeder erfolgreich erneuerten Antwort automatisch nach.
  useEffect(() => {
    if (!zugriff) return;
    window.clearTimeout(timerRef.current);
    const rest = restlaufzeitMs(zugriff.expiresAt) - ErneuerungsVorlaufMs;
    const wartezeit = rest > 5000 ? rest : 5000;
    timerRef.current = window.setTimeout(() => {
      void laden(undefined, true);
    }, wartezeit);
    return () => window.clearTimeout(timerRef.current);
  }, [zugriff, laden]);

  useEffect(() => {
    const abbruch = new AbortController();
    void laden(abbruch.signal);
    return () => abbruch.abort();
  }, [laden]);

  useEffect(() => () => window.clearTimeout(timerRef.current), []);

  return { zugriff, fehler, busy, erneutVersuchen: () => void laden() };
}

type BildInfo = {
  zugriff: DateiZugriff;
  dokument: Dokument;
};

// Fotografie im Lesesaal: das Bild trägt seine Beschreibung als
// Bildunterschrift und als Vorlesetext; das Aufklappen der Großansicht
// bleibt beim Betrachter. Der Redaktion bleibt die Beschreibung
// korrigierbar (Beschreibung wird zum alt-Text).
function BildEintrag({
  auftrittId,
  dokument,
  isEditor,
  onVergroessern,
  onBearbeitet,
  aktualisieren,
}: {
  auftrittId: string;
  dokument: Dokument;
  isEditor: boolean;
  onVergroessern: (bild: BildInfo) => void;
  onBearbeitet: () => void;
  aktualisieren: () => void;
}) {
  const { zugriff, fehler, busy, erneutVersuchen } = useTicketzugriff(
    dokument.id,
  );
  const stillerVersuch = useRef(false);
  const [bildFehler, setBildFehler] = useState("");
  const [bearbeiten, setBearbeiten] = useState(false);
  const beschriftung = beschriftungVon(dokument);
  const altText = altTextVon(dokument);

  async function beiBildFehler() {
    // Abgelaufene Tickets äussern sich als Ladefehler: erst still erneuern,
    // dann eine verständliche Meldung zeigen.
    if (!stillerVersuch.current) {
      stillerVersuch.current = true;
      await erneutVersuchen();
      return;
    }
    setBildFehler(
      "Das Bild konnte nicht geladen werden. Bitte erneut versuchen.",
    );
  }

  // Bildunterschrift als letztes fig-Kind; Meta-Zeile und Redaktions-
  // werkzeug laufen außerhalb der Figur (figcaption muss erstes oder
  // letztes Kind der figure sein).
  return (
    <>
      <figure className="dokument-foto">
        {bildFehler ? (
          <>
            <p role="alert" className="feld-fehler">
              {bildFehler}
            </p>
            <div className="noten-aktionen">
              <button
                type="button"
                onClick={() => {
                  stillerVersuch.current = false;
                  setBildFehler("");
                  erneutVersuchen();
                }}
              >
                Erneut versuchen
              </button>
            </div>
          </>
        ) : zugriff ? (
          <button
            type="button"
            className="dokument-foto-oeffnen"
            onClick={() => onVergroessern({ zugriff, dokument })}
            aria-label={`${beschriftung} vergrößern`}
          >
            {/* biome-ignore lint/performance/noImgElement: Signierte Ticket-URLs privater
                Aufnahmen dürfen nicht durch den Bildoptimierer laufen (Zwischenspeicherung). */}
            <img
              className="dokument-foto-bild"
              src={zugriff.viewUrl}
              alt={altText}
              loading="lazy"
              decoding="async"
              onLoad={() => {
                stillerVersuch.current = false;
              }}
              onError={() => void beiBildFehler()}
            />
          </button>
        ) : fehler ? (
          <>
            <p role="alert" className="feld-fehler">
              {fehler}
            </p>
            <div className="noten-aktionen">
              <button type="button" onClick={erneutVersuchen} disabled={busy}>
                Erneut versuchen
              </button>
            </div>
          </>
        ) : (
          <p className="auftritt-leer">Bild wird vorbereitet …</p>
        )}
        <figcaption className="dokument-bildunterschrift">
          {beschriftung}
        </figcaption>
      </figure>
      <p className="noten-info">
        {typText(dokument.contentType)} · {groesseText(dokument.sizeBytes)}
      </p>
      {isEditor && (
        <RedaktionsWerkzeug
          auftrittId={auftrittId}
          dokument={dokument}
          bearbeiten={bearbeiten}
          onUmschalten={() => setBearbeiten(!bearbeiten)}
          onBearbeitet={() => {
            setBearbeiten(false);
            onBearbeitet();
          }}
          aktualisieren={aktualisieren}
        />
      )}
    </>
  );
}

// Dokument im Lesesaal: beschrifteter Eintrag mit den beiden Ticket-Aktionen
// „Öffnen“ (Ansicht im neuen Tab) und „Herunterladen“; der Redaktion bleibt
// die Beschreibung korrigierbar.
function DokumentEintrag({
  auftrittId,
  dokument,
  isEditor,
  onBearbeitet,
  aktualisieren,
}: {
  auftrittId: string;
  dokument: Dokument;
  isEditor: boolean;
  onBearbeitet: () => void;
  aktualisieren: () => void;
}) {
  const { zugriff, fehler, busy, erneutVersuchen } = useTicketzugriff(
    dokument.id,
  );
  const [bearbeiten, setBearbeiten] = useState(false);
  const beschriftung = beschriftungVon(dokument);

  return (
    <article className="material-eintrag" aria-label={beschriftung}>
      <p className="dokument-titel">{beschriftung}</p>
      <p className="noten-info">
        {typText(dokument.contentType)} · {groesseText(dokument.sizeBytes)}
      </p>
      <div className="noten-aktionen">
        {zugriff ? (
          <>
            <a
              className="noten-laden"
              href={zugriff.viewUrl}
              target="_blank"
              rel="noreferrer"
            >
              Öffnen
            </a>
            <a className="noten-laden" href={zugriff.downloadUrl} download>
              Herunterladen
            </a>
          </>
        ) : (
          <button
            type="button"
            onClick={erneutVersuchen}
            disabled={busy}
            aria-label={`${beschriftung} vorbereiten`}
          >
            {busy ? "Wird vorbereitet …" : "Öffnen"}
          </button>
        )}
      </div>
      {fehler && (
        <p role="alert" className="feld-fehler">
          {fehler}{" "}
          <button type="button" onClick={erneutVersuchen} disabled={busy}>
            Erneut versuchen
          </button>
        </p>
      )}
      {isEditor && (
        <RedaktionsWerkzeug
          auftrittId={auftrittId}
          dokument={dokument}
          bearbeiten={bearbeiten}
          onUmschalten={() => setBearbeiten(!bearbeiten)}
          onBearbeitet={() => {
            setBearbeiten(false);
            onBearbeitet();
          }}
          aktualisieren={aktualisieren}
        />
      )}
    </article>
  );
}

// Redaktionswerkzeug eines Eintrags: Beschreibung bearbeiten, Datei ersetzen,
// Datei in den Papierkorb legen. Ersetzte und gelöschte Dateien wandern in den
// Papierkorb (#168); der Lesesaal zeigt danach nur den aktuellen Stand.
function RedaktionsWerkzeug({
  auftrittId,
  dokument,
  bearbeiten,
  onUmschalten,
  onBearbeitet,
  aktualisieren,
}: {
  auftrittId: string;
  dokument: Dokument;
  bearbeiten: boolean;
  onUmschalten: () => void;
  onBearbeitet: () => void;
  aktualisieren: () => void;
}) {
  const beschriftung = beschriftungVon(dokument);
  const idPraefix = `dokument-${dokument.id}`;
  const eingabeRef = useRef<HTMLInputElement>(null);
  const [beschreibung, setBeschreibung] = useState(dokument.description ?? "");
  const [busy, setBusy] = useState(false);
  const [bestaetigen, setBestaetigen] = useState(false);
  const [status, setStatus] = useState("");
  const [fehler, setFehler] = useState("");

  async function speichern() {
    setBusy(true);
    setFehler("");
    try {
      await aendereDateiBeschreibung(dokument.id, beschreibung.trim());
      aktualisieren();
      onBearbeitet();
    } catch (ursache) {
      setFehler(
        await problemTitel(
          ursache,
          "Das hat nicht geklappt. Bitte erneut versuchen.",
        ),
      );
    } finally {
      setBusy(false);
    }
  }

  async function ersetzen(event: React.ChangeEvent<HTMLInputElement>) {
    const gewaehlt = event.target.files?.[0];
    event.target.value = "";
    if (!gewaehlt) return;
    setBusy(true);
    setFehler("");
    setStatus("Neue Datei wird übertragen …");
    try {
      await ladeAuftrittDateiHoch(
        auftrittId,
        auftrittUploadSchluessel(auftrittId, `ersetzen-${dokument.id}`),
        {
          kind: "sonstiges",
          contentType: contentTypeFuerDokument(gewaehlt),
          replacesDateiId: dokument.id,
        },
        gewaehlt,
        (schritt) =>
          setStatus(
            schritt === "uebertragen"
              ? "Neue Datei wird übertragen …"
              : "Neue Datei wird geprüft …",
          ),
      );
      setStatus("");
      aktualisieren();
    } catch (ursache) {
      setStatus("");
      setFehler(
        ursache instanceof AbbruchFehler
          ? "Das Ersetzen wurde abgebrochen."
          : ursache instanceof MaterialFehler
            ? ursache.message
            : await problemTitel(
                ursache,
                "Das Ersetzen konnte nicht abgeschlossen werden. Bitte erneut versuchen.",
              ),
      );
    } finally {
      setBusy(false);
    }
  }

  async function loeschen() {
    setBusy(true);
    setFehler("");
    try {
      await loescheDatei(dokument.id, dokument.rowVersion);
      aktualisieren();
    } catch (ursache) {
      setBestaetigen(false);
      const veraltet = ursache instanceof Response && ursache.status === 409;
      setFehler(
        veraltet
          ? "Die Datei wurde inzwischen geändert. Die Liste wird neu geladen."
          : await problemTitel(
              ursache,
              "Die Datei konnte nicht gelöscht werden.",
            ),
      );
      if (veraltet) aktualisieren();
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="noten-aktionen">
        <button
          type="button"
          className="knopf-leise"
          aria-expanded={bearbeiten}
          onClick={onUmschalten}
        >
          {bearbeiten ? "Bearbeiten schließen" : "Beschreibung bearbeiten"}
        </button>
      </div>
      {bearbeiten && (
        <div className="material-bearbeiten">
          <label htmlFor={`${idPraefix}-beschreibung`}>
            Beschreibung (wird als Text zum Bild vorgelesen)
          </label>
          <input
            id={`${idPraefix}-beschreibung`}
            type="text"
            maxLength={500}
            value={beschreibung}
            onChange={(event) => setBeschreibung(event.target.value)}
          />
          {fehler && (
            <p role="alert" className="feld-fehler">
              {fehler}
            </p>
          )}
          <div className="auth-aktionen">
            <button
              type="button"
              disabled={busy}
              onClick={() => void speichern()}
            >
              {busy ? "Wird gespeichert …" : "Änderungen speichern"}
            </button>
          </div>
        </div>
      )}
      <div className="datei-aktionen">
        <input
          ref={eingabeRef}
          type="file"
          hidden
          aria-label={`Neue Datei für ${beschriftung}`}
          accept=".pdf,image/jpeg,image/png,image/webp"
          onChange={(event) => void ersetzen(event)}
        />
        {bestaetigen ? (
          <>
            <span className="noten-info">
              „{beschriftung}“ in den Papierkorb legen?
            </span>
            <button
              type="button"
              onClick={() => void loeschen()}
              disabled={busy}
            >
              Ja, löschen
            </button>
            <button
              type="button"
              className="knopf-leise"
              onClick={() => setBestaetigen(false)}
              disabled={busy}
            >
              Abbrechen
            </button>
          </>
        ) : (
          <>
            <button
              type="button"
              className="knopf-leise"
              onClick={() => eingabeRef.current?.click()}
              disabled={busy}
              aria-label={`Ersetzen · ${beschriftung}`}
            >
              Ersetzen
            </button>
            <button
              type="button"
              className="knopf-leise"
              onClick={() => setBestaetigen(true)}
              disabled={busy}
              aria-label={`Löschen · ${beschriftung}`}
            >
              Löschen
            </button>
          </>
        )}
        {status && (
          <output aria-live="polite" className="noten-info">
            {status}
          </output>
        )}
        {fehler && !bearbeiten && (
          <output aria-live="polite" className="feld-fehler">
            {fehler}
          </output>
        )}
      </div>
    </>
  );
}

type MaterialStatus =
  | "warten"
  | "uebertragen"
  | "geprueft"
  | "gespeichert"
  | "gescheitert"
  | "abbruchLaeuft"
  | "abgebrochen";

type DateiZeile = {
  id: number;
  datei: File;
  beschreibung: string;
  status: MaterialStatus;
  fortschritt: number;
  fehler: string;
};

type Fortsetzung = { schluessel: string; fileName: string };

// Zustandswörter der Werkbank, wie im Notenbereich.
function statusTextFuer(status: MaterialStatus, fortschritt: number): string {
  switch (status) {
    case "warten":
      return "wartet";
    case "uebertragen":
      return `wird übertragen … ${Math.round(fortschritt)} %`;
    case "geprueft":
      return "wird geprüft …";
    case "gespeichert":
      return "gespeichert.";
    case "gescheitert":
      return "gescheitert";
    case "abbruchLaeuft":
      return "wird abgebrochen …";
    case "abgebrochen":
      return "abgebrochen";
  }
}

async function fehlerMeldung(
  ursache: unknown,
  ersatz: string,
): Promise<string> {
  if (ursache instanceof MaterialFehler) return ursache.message;
  return problemTitel(ursache, ersatz);
}

function speicherFuer(auftrittId: string, datei: File): string {
  return auftrittUploadSchluessel(
    auftrittId,
    `${DokumentPlatz}${datei.name.toLowerCase()}`,
  );
}

export function AuftrittDokumente({
  auftritt,
  isEditor,
  aktualisieren,
}: {
  auftritt: AuftrittDetails;
  isEditor: boolean;
  aktualisieren: () => void;
}) {
  // Tolerant gegenüber Antworten ohne Materialfeld (älterer Stand).
  const dokumente = useMemo(
    () => auftritt.documents ?? [],
    [auftritt.documents],
  );
  const eingabeRef = useRef<HTMLInputElement>(null);
  const naechsteZeileId = useRef(0);
  const zeilenRef = useRef<DateiZeile[]>([]);
  const abbrueche = useRef(new Map<number, AbortController>());
  // Ein Wunsch je Mausklick: eine unterbrochene Übertragung, deren Datei
  // erneut gewählt wird, wird fortgesetzt.
  const wunschRef = useRef<Fortsetzung | null>(null);
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const [zeilen, setZeilen] = useState<DateiZeile[]>([]);
  const [uebertragLaeuft, setUebertragLaeuft] = useState(false);
  const [batchMeldung, setBatchMeldung] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [fortsetzbar, setFortsetzbar] = useState<Fortsetzung[]>([]);
  const [gross, setGross] = useState<BildInfo | null>(null);

  // Unterbrochene Übertragungen des lokalen Speichers.
  useEffect(() => {
    if (!isEditor) return;
    setFortsetzbar(
      unterbrocheneAuftrittUebertragungen(auftritt.id, DokumentPlatz).filter(
        (eintrag) =>
          !zeilenRef.current.some(
            (zeile) =>
              speicherFuer(auftritt.id, zeile.datei) === eintrag.schluessel,
          ),
      ),
    );
  }, [auftritt.id, isEditor]);

  useEffect(() => {
    if (!gross) return;
    dialogRef.current?.showModal();
  }, [gross]);

  const fotos = dokumente.filter((dokument) => dokument.art === "photo");
  const dokumentEintraege = dokumente.filter(
    (dokument) => dokument.art === "document",
  );

  function zeileAendern(id: number, aenderung: Partial<DateiZeile>) {
    const stand = zeilenRef.current.map((zeile) =>
      zeile.id === id ? { ...zeile, ...aenderung } : zeile,
    );
    zeilenRef.current = stand;
    setZeilen(stand);
  }

  function dateienHinzufuegen(liste: FileList | File[]) {
    const neue: DateiZeile[] = Array.from(liste).map((datei) => ({
      id: naechsteZeileId.current++,
      datei,
      beschreibung: "",
      status: "warten",
      fortschritt: 0,
      fehler: "",
    }));
    if (neue.length === 0) return;
    setBatchMeldung("");
    const stand = [...zeilenRef.current, ...neue];
    zeilenRef.current = stand;
    setZeilen(stand);
  }

  function eingabeWaehlen(event: React.ChangeEvent<HTMLInputElement>) {
    const liste = Array.from(event.target.files ?? []);
    event.target.value = "";
    if (liste.length === 0) return;
    const wunsch = wunschRef.current;
    wunschRef.current = null;
    if (wunsch) {
      void setzeFort(wunsch, liste[0]);
      return;
    }
    dateienHinzufuegen(liste);
  }

  function ablegen(event: React.DragEvent<HTMLElement>) {
    event.preventDefault();
    if (!event.dataTransfer.files.length) return;
    dateienHinzufuegen(event.dataTransfer.files);
  }

  function ziehStart(event: React.DragEvent<HTMLElement>) {
    event.preventDefault();
  }

  async function starteZeile(zeile: DateiZeile, fortsetzen = false) {
    const abbruch = new AbortController();
    abbrueche.current.set(zeile.id, abbruch);
    zeileAendern(zeile.id, {
      status: "uebertragen",
      fehler: "",
      fortschritt: 0,
    });
    const speicher = speicherFuer(auftritt.id, zeile.datei);
    const inhaltstyp = contentTypeFuerDokument(zeile.datei);
    const optionen = {
      onFortschritt: (uebertragenBytes: number, gesamtBytes: number) => {
        zeileAendern(zeile.id, {
          fortschritt: (uebertragenBytes / gesamtBytes) * 100,
        });
      },
      signal: abbruch.signal,
    };
    const beiSchritt = (schritt: MaterialSchritt) => {
      zeileAendern(zeile.id, { status: schritt });
    };
    try {
      if (fortsetzen) {
        await setzeAuftrittUebertragungFort(
          speicher,
          inhaltstyp,
          zeile.datei,
          beiSchritt,
          optionen,
        );
      } else {
        await ladeAuftrittDateiHoch(
          auftritt.id,
          speicher,
          {
            kind: "sonstiges",
            contentType: inhaltstyp,
            description: zeile.beschreibung.trim() || undefined,
          },
          zeile.datei,
          beiSchritt,
          optionen,
        );
      }
      zeileAendern(zeile.id, { status: "gespeichert", fehler: "" });
    } catch (ursache) {
      const abgebrochen =
        abbruch.signal.aborted || ursache instanceof AbbruchFehler;
      zeileAendern(zeile.id, {
        status: abgebrochen ? "abgebrochen" : "gescheitert",
        fehler: abgebrochen ? "" : await fehlerMeldung(ursache, startMeldung),
      });
    } finally {
      abbrueche.current.delete(zeile.id);
    }
  }

  function abbrechen(zeile: DateiZeile) {
    if (abbrueche.current.has(zeile.id)) {
      zeileAendern(zeile.id, { status: "abbruchLaeuft" });
      abbrueche.current.get(zeile.id)?.abort();
    }
  }

  async function setzeFort(wunsch: Fortsetzung, datei: File) {
    const eintrag = liesUploadSitzung(wunsch.schluessel);
    const identisch =
      eintrag !== null &&
      datei.name === eintrag.fileName &&
      datei.size === eintrag.sizeBytes &&
      datei.lastModified === eintrag.lastModified;
    if (eintrag && !identisch) {
      // Eine andere Datei als die gemerkte: die alte Sitzung wird
      // abgebrochen, der Upload startet mit einer frischen Zeile.
      entferneUploadSitzung(wunsch.schluessel);
      void cancelUploadSession(eintrag.uploadSessionId).catch(() => {});
    }
    const zeile: DateiZeile = {
      id: naechsteZeileId.current++,
      datei,
      beschreibung: "",
      status: "warten",
      fortschritt: 0,
      fehler: "",
    };
    zeilenRef.current = [...zeilenRef.current, zeile];
    setZeilen(zeilenRef.current);
    setFortsetzbar((vorher) =>
      vorher.filter((alt) => alt.schluessel !== wunsch.schluessel),
    );
    setUebertragLaeuft(true);
    await starteZeile(zeile, identisch);
    setUebertragLaeuft(false);
    batchMeldungNeu(zeilenRef.current);
    aktualisieren();
  }

  function batchMeldungNeu(stand: DateiZeile[]) {
    const gesamt = stand.length;
    const gesichert = stand.filter(
      (zeile) => zeile.status === "gespeichert",
    ).length;
    if (gesamt === 0) {
      setBatchMeldung("");
      return;
    }
    if (gesichert === gesamt) {
      setBatchMeldung(`${gesichert} von ${gesamt} Dateien gespeichert.`);
      return;
    }
    setBatchMeldung(
      `${gesichert} von ${gesamt} Dateien gespeichert. Gescheiterte Dateien können erneut übertragen werden.`,
    );
  }

  async function uebertragen() {
    if (uebertragLaeuft) return;
    const abrechnung = zeilenRef.current.filter(
      (zeile) => zeile.status !== "gespeichert",
    );
    if (abrechnung.length === 0) return;
    const versucht = new Set(abrechnung.map((zeile) => zeile.id));
    setUebertragLaeuft(true);
    let naechster = 0;
    const arbeiter = async () => {
      while (naechster < abrechnung.length) {
        const zeile = abrechnung[naechster++];
        await starteZeile(zeile);
      }
    };
    await Promise.all([arbeiter(), arbeiter()]);
    setUebertragLaeuft(false);
    batchMeldungNeu(
      zeilenRef.current.filter((zeile) => versucht.has(zeile.id)),
    );
    aktualisieren();
  }

  async function erneutVersuchen(id: number) {
    const zeile = zeilenRef.current.find((eintrag) => eintrag.id === id);
    if (!zeile) return;
    setUebertragLaeuft(true);
    await starteZeile(zeile);
    setUebertragLaeuft(false);
    batchMeldungNeu(
      zeilenRef.current.filter(
        (eintrag) => eintrag.id === id || eintrag.status === "gespeichert",
      ),
    );
    aktualisieren();
  }

  const gesamt = zeilen.length;
  const gesichert = zeilen.filter(
    (zeile) => zeile.status === "gespeichert",
  ).length;
  const materialLiegt = fotos.length > 0 || dokumentEintraege.length > 0;

  return (
    <section
      className="auftritt-abschnitt"
      aria-labelledby="auftritt-dokumente-titel"
    >
      <h3 id="auftritt-dokumente-titel">Dokumente</h3>
      {erfolg && (
        <output aria-live="polite" className="auth-erfolg">
          {erfolg}
        </output>
      )}
      {materialLiegt ? (
        <>
          {fotos.length > 0 && (
            <ul className="dokumente-fotos">
              {fotos.map((foto) => (
                <li key={foto.id}>
                  <BildEintrag
                    auftrittId={auftritt.id}
                    dokument={foto}
                    isEditor={isEditor}
                    onVergroessern={setGross}
                    onBearbeitet={() => setErfolg("Änderungen gespeichert.")}
                    aktualisieren={aktualisieren}
                  />
                </li>
              ))}
            </ul>
          )}
          {dokumentEintraege.length > 0 && (
            <div className="material-liste dokumente-liste">
              {dokumentEintraege.map((eintrag) => (
                <DokumentEintrag
                  auftrittId={auftritt.id}
                  dokument={eintrag}
                  isEditor={isEditor}
                  onBearbeitet={() => setErfolg("Änderungen gespeichert.")}
                  aktualisieren={aktualisieren}
                  key={eintrag.id}
                />
              ))}
            </div>
          )}
        </>
      ) : (
        <p className="auftritt-leer">
          Zu diesem Auftritt sind noch keine Dokumente hinterlegt.
        </p>
      )}

      {isEditor && (
        <details
          className="material-verwaltung"
          aria-labelledby="dokument-verwaltung-titel"
        >
          <summary>
            <h2 id="dokument-verwaltung-titel">
              Dokumente und Fotografien verwalten
            </h2>
            <span className="material-verwaltung-umschalter" aria-hidden="true">
              <span className="material-verwaltung-auf">Ausklappen</span>
              <span className="material-verwaltung-zu">Einklappen</span>
            </span>
          </summary>
          <fieldset
            className="material-verwaltung"
            aria-label="Dokumente und Fotografien verwalten"
            onDrop={ablegen}
            onDragOver={ziehStart}
          >
            {batchMeldung && (
              <output aria-live="polite" className="auth-erfolg">
                {batchMeldung}
              </output>
            )}
            {fortsetzbar.length > 0 && (
              <div className="material-fortsetzungen">
                <p>Unterbrochene Übertragungen</p>
                {fortsetzbar.map((wunsch) => (
                  <div className="material-fortsetzung" key={wunsch.schluessel}>
                    <span className="material-datei-name">
                      {wunsch.fileName}
                    </span>
                    <button
                      type="button"
                      disabled={uebertragLaeuft}
                      onClick={() => {
                        wunschRef.current = wunsch;
                        eingabeRef.current?.click();
                      }}
                    >
                      Fortsetzen
                    </button>
                  </div>
                ))}
              </div>
            )}
            {zeilen.length > 0 && (
              <ul className="material-dateien">
                {zeilen.map((zeile) => (
                  <li
                    className="material-datei"
                    key={zeile.id}
                    data-status={zeile.status}
                  >
                    <div className="material-datei-kopf">
                      <span className="material-datei-name">
                        {zeile.datei.name}
                      </span>
                      <span className="material-datei-groesse">
                        {groesseText(zeile.datei.size)}
                      </span>
                      <span
                        className="material-datei-status"
                        aria-live="polite"
                      >
                        {statusTextFuer(zeile.status, zeile.fortschritt)}
                      </span>
                    </div>
                    {(zeile.status === "uebertragen" ||
                      zeile.status === "geprueft") && (
                      <div className="material-fortschritt" aria-hidden="true">
                        <span
                          style={{
                            width: `${Math.max(0, Math.min(100, zeile.fortschritt))}%`,
                          }}
                        />
                      </div>
                    )}
                    {(zeile.status === "uebertragen" ||
                      zeile.status === "geprueft" ||
                      zeile.status === "abbruchLaeuft") && (
                      <button
                        type="button"
                        className="knopf-leise"
                        onClick={() => abbrechen(zeile)}
                        disabled={zeile.status === "abbruchLaeuft"}
                      >
                        {zeile.status === "abbruchLaeuft"
                          ? "Wird abgebrochen …"
                          : "Abbrechen"}
                      </button>
                    )}
                    {(zeile.status === "gescheitert" ||
                      zeile.status === "abgebrochen") && (
                      <>
                        <output aria-live="polite" className="feld-fehler">
                          {zeile.fehler}
                        </output>
                        <button
                          type="button"
                          onClick={() => void erneutVersuchen(zeile.id)}
                          disabled={uebertragLaeuft}
                        >
                          Erneut versuchen
                        </button>
                      </>
                    )}
                    <div className="material-felder">
                      <label
                        htmlFor={`dokument-zeile-${zeile.id}-beschreibung`}
                      >
                        Beschreibung (wird als Text zum Bild vorgelesen)
                      </label>
                      <input
                        id={`dokument-zeile-${zeile.id}-beschreibung`}
                        type="text"
                        maxLength={500}
                        value={zeile.beschreibung}
                        disabled={zeile.status !== "warten"}
                        onChange={(event) =>
                          zeileAendern(zeile.id, {
                            beschreibung: event.target.value,
                          })
                        }
                      />
                    </div>
                  </li>
                ))}
              </ul>
            )}
            <div className="noten-aktionen">
              <button
                type="button"
                onClick={() => {
                  wunschRef.current = null;
                  eingabeRef.current?.click();
                }}
                disabled={uebertragLaeuft}
              >
                Dokument/Fotografie hinzufügen
              </button>
              <button
                type="button"
                onClick={() => void uebertragen()}
                disabled={uebertragLaeuft || gesichert === gesamt}
              >
                Material übertragen
              </button>
              <input
                ref={eingabeRef}
                className="visually-hidden"
                type="file"
                multiple
                accept=".pdf,image/jpeg,image/png,image/webp"
                onChange={eingabeWaehlen}
                disabled={uebertragLaeuft}
                tabIndex={-1}
                aria-hidden="true"
              />
            </div>
          </fieldset>
        </details>
      )}

      {gross && (
        <dialog
          ref={dialogRef}
          className="auftritt-grossansicht"
          aria-labelledby="auftritt-grossansicht-beschriftung"
          onClose={() => setGross(null)}
        >
          {/* biome-ignore lint/performance/noImgElement: Signierte Ticket-URLs privater
              Aufnahmen dürfen nicht durch den Bildoptimierer laufen (Zwischenspeicherung). */}
          <img src={gross.zugriff.viewUrl} alt={altTextVon(gross.dokument)} />
          <p
            id="auftritt-grossansicht-beschriftung"
            className="dokument-bildunterschrift"
          >
            {beschriftungVon(gross.dokument)}
          </p>
          <p className="noten-info">
            {typText(gross.dokument.contentType)} ·{" "}
            {groesseText(gross.dokument.sizeBytes)}
          </p>
          <div className="noten-aktionen">
            <button type="button" onClick={() => dialogRef.current?.close()}>
              Schließen
            </button>
          </div>
        </dialog>
      )}
    </section>
  );
}
