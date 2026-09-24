"use client";

// ARC-025: Dokumente und Fotografien am Auftritt — Lesesaal (Fotografien
// mit Bildunterschrift und Vorlesetext, Dokumente mit Öffnen/Herunterladen)
// und die Redaktions-Werkbank nach der Rezeptur des Notenbereichs
// (Anlage → Upload-Sitzung → Übertragung zum Ticket → Abschluss,
// Fortschritt je Zeile, Erneut-versuchen über das bestehende Material,
// Fortsetzen unterbrochener Übertragungen aus dem lokalen Speicher).

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  AbbruchFehler,
  type AssetAccessResponse,
  cancelUploadSession,
  entferneUploadSitzung,
  fetchAssetAccess,
  type GespeicherteUploadSitzung,
  groesseText,
  liesUploadSitzung,
  MaterialFehler,
  type MaterialSchritt,
  problemTitel,
  restlaufzeitMs,
  setzeUploadFort,
  uebertrageDatei,
} from "@/lib/assets";
import {
  type AuftrittDetails,
  contentTypeFuerDokument,
  createEventAsset,
  type Dokument,
  type DokumentRevision,
  type DokumentTyp,
  dokumentTypFuerDatei,
  dokumentTypName,
  istDokumentTyp,
  patchEventAsset,
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

// Tickets werden pro Material geholt: entfernte Dateien (404) erklären
// sich selbst, abgelaufene Anmeldungen weisen aufs Neuladen, Vorübergehendes
// bietet den erneuten Versuch (Rezeptur lib/assets.ts).
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
  return dokument.description?.trim() || dokumentTypName(dokument.assetType);
}

/** Vorlesetext des Bildes: die Beschreibung oder der menschliche Ersatz. */
function altTextVon(dokument: Dokument): string {
  return dokument.description?.trim() || "Fotografie zum Auftritt";
}

// Die Ticket-Abfrage je Material: einmal je Eintrag, still erneuert, bevor
// das Ticket abläuft (Rezeptur des Audio-Spielers), mit eigenen
// Fehlerzuständen pro Eintrag.
function useTicketzugriff(assetId: string) {
  const [zugriff, setZugriff] = useState<AssetAccessResponse | null>(null);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(true);
  const timerRef = useRef<number | undefined>(undefined);

  const laden = useCallback(
    async (signal?: AbortSignal, still = false) => {
      try {
        const neu = await fetchAssetAccess(assetId, signal);
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
    [assetId],
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
  zugriff: AssetAccessResponse;
  dokument: Dokument;
};

// Fotografie im Lesesaal: das Bild trägt seine Beschreibung als
// Bildunterschrift und als Vorlesetext; das Aufklappen der Großansicht
// bleibt beim Betrachter. Der Redaktion bleibt die Beschreibung
// korrigierbar (Beschreibung wird zum alt-Text).
function BildEintrag({
  dokument,
  isEditor,
  onVergroessern,
  onBearbeitet,
  aktualisieren,
}: {
  dokument: Dokument & { currentRevision: DokumentRevision };
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
        {typText(dokument.currentRevision.contentType)} ·{" "}
        {groesseText(dokument.currentRevision.sizeBytes)}
      </p>
      {isEditor && (
        <>
          <div className="noten-aktionen">
            <button
              type="button"
              className="knopf-leise"
              aria-expanded={bearbeiten}
              onClick={() => setBearbeiten(!bearbeiten)}
            >
              {bearbeiten ? "Bearbeiten schließen" : "Beschreibung bearbeiten"}
            </button>
          </div>
          {bearbeiten && (
            <MaterialBearbeiten
              dokument={dokument}
              onFertig={() => {
                setBearbeiten(false);
                onBearbeitet();
              }}
              aktualisieren={aktualisieren}
            />
          )}
        </>
      )}
    </>
  );
}

// Dokument im Lesesaal: beschrifteter Eintrag mit den beiden Ticket-Aktionen
// „Öffnen“ (Ansicht im neuen Tab) und „Herunterladen“; der Redaktion bleibt
// die Beschreibung korrigierbar.
function DokumentEintrag({
  dokument,
  isEditor,
  onBearbeitet,
  aktualisieren,
}: {
  dokument: Dokument & { currentRevision: DokumentRevision };
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
        {typText(dokument.currentRevision.contentType)} ·{" "}
        {groesseText(dokument.currentRevision.sizeBytes)}
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
        <>
          <div className="noten-aktionen">
            <button
              type="button"
              className="knopf-leise"
              aria-expanded={bearbeiten}
              onClick={() => setBearbeiten(!bearbeiten)}
            >
              {bearbeiten ? "Bearbeiten schließen" : "Beschreibung bearbeiten"}
            </button>
          </div>
          {bearbeiten && (
            <MaterialBearbeiten
              dokument={dokument}
              onFertig={() => {
                setBearbeiten(false);
                onBearbeitet();
              }}
              aktualisieren={aktualisieren}
            />
          )}
        </>
      )}
    </article>
  );
}

// Bearbeiten-Formular eines Eintrags: Beschreibung immer, Materialart nur
// solange keine Datei hochgeladen ist (der Server sperrt sie danach mit 409).
function MaterialBearbeiten({
  dokument,
  onFertig,
  aktualisieren,
}: {
  dokument: Dokument;
  onFertig: () => void;
  aktualisieren: () => void;
}) {
  const typGesperrt = dokument.currentRevision !== null;
  const idPraefix = `dokument-${dokument.id}`;
  const [typ, setTyp] = useState<DokumentTyp>(dokument.assetType);
  const [beschreibung, setBeschreibung] = useState(dokument.description ?? "");
  const [busy, setBusy] = useState(false);
  const [fehler, setFehler] = useState("");

  async function speichern() {
    setBusy(true);
    setFehler("");
    try {
      const body: { assetType?: DokumentTyp; description: string } = {
        description: beschreibung.trim(),
      };
      if (!typGesperrt) body.assetType = typ;
      await patchEventAsset(dokument.id, body);
      aktualisieren();
      onFertig();
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

  return (
    <div className="material-bearbeiten">
      {typGesperrt ? (
        <p className="noten-info">
          Materialtyp: {dokumentTypName(dokument.assetType)} (nach dem ersten
          Hochladen gesperrt)
        </p>
      ) : (
        <>
          <label htmlFor={`${idPraefix}-typ`}>Materialtyp</label>
          <select
            id={`${idPraefix}-typ`}
            value={typ}
            onChange={(event) => setTyp(event.target.value as DokumentTyp)}
          >
            <option value="document">Dokument (PDF)</option>
            <option value="photo">Fotografie</option>
          </select>
        </>
      )}
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
        <button type="button" disabled={busy} onClick={() => void speichern()}>
          {busy ? "Wird gespeichert …" : "Änderungen speichern"}
        </button>
      </div>
    </div>
  );
}

// Zeile für noch nicht hochgeladenes Material: die Redaktion kann den
// Upload (erneut) starten und Beschreibung/Typ über PATCH nachbessern.
function UnausgeliefertZeile({
  dokument,
  busy,
  onHochladen,
  onBearbeitet,
  aktualisieren,
}: {
  dokument: Dokument;
  busy: boolean;
  onHochladen: (dokument: Dokument) => void;
  onBearbeitet: () => void;
  aktualisieren: () => void;
}) {
  const [bearbeiten, setBearbeiten] = useState(false);

  return (
    <div className="material-fortsetzung">
      <span className="material-datei-name">{beschriftungVon(dokument)}</span>
      <span className="material-datei-groesse">
        {dokumentTypName(dokument.assetType)}
      </span>
      <button
        type="button"
        onClick={() => onHochladen(dokument)}
        disabled={busy}
      >
        Hochladen
      </button>
      <button
        type="button"
        className="knopf-leise"
        aria-expanded={bearbeiten}
        onClick={() => setBearbeiten(!bearbeiten)}
      >
        {bearbeiten ? "Bearbeiten schließen" : "Bearbeiten"}
      </button>
      {bearbeiten && (
        <MaterialBearbeiten
          dokument={dokument}
          onFertig={() => {
            setBearbeiten(false);
            onBearbeitet();
          }}
          aktualisieren={aktualisieren}
        />
      )}
    </div>
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

type FortsetzungsWunsch = {
  assetId: string;
  assetTyp: DokumentTyp;
  eintrag: GespeicherteUploadSitzung;
};

type DateiZeile = {
  id: number;
  datei: File;
  assetTyp: DokumentTyp;
  beschreibung: string;
  assetId: string | null;
  status: MaterialStatus;
  fortschritt: number;
  fehler: string;
};

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
  // Ein Wunsch je Mausklick: eine unterbrochene Übertragung (mit gemerkter
  // Sitzung) oder ein unausgelieferter Eintrag, an den die gewählte Datei
  // gebunden wird.
  const wunschRef = useRef<FortsetzungsWunsch | null>(null);
  const verbindungsRef = useRef<{
    assetId: string;
    assetTyp: DokumentTyp;
    beschreibung: string;
  } | null>(null);
  const dialogRef = useRef<HTMLDialogElement | null>(null);
  const [zeilen, setZeilen] = useState<DateiZeile[]>([]);
  const [uebertragLaeuft, setUebertragLaeuft] = useState(false);
  const [batchMeldung, setBatchMeldung] = useState("");
  const [erfolg, setErfolg] = useState("");
  const [fortsetzbar, setFortsetzbar] = useState<FortsetzungsWunsch[]>([]);
  const [gross, setGross] = useState<BildInfo | null>(null);

  // Unterbrochene Übertragungen des lokalen Speichers; fertige Materialien
  // räumen ihren gespeicherten Stand weg.
  useEffect(() => {
    if (!isEditor) return;
    const eintraege: FortsetzungsWunsch[] = [];
    for (const dokument of dokumente) {
      if (!istDokumentTyp(dokument.assetType)) continue;
      if (dokument.currentRevision !== null) {
        entferneUploadSitzung(dokument.id);
        continue;
      }
      const eintrag = liesUploadSitzung(dokument.id);
      if (
        eintrag &&
        !zeilenRef.current.some((zeile) => zeile.assetId === dokument.id)
      ) {
        eintraege.push({
          assetId: dokument.id,
          assetTyp: dokument.assetType,
          eintrag,
        });
      }
    }
    setFortsetzbar(eintraege);
  }, [dokumente, isEditor]);

  useEffect(() => {
    if (!gross) return;
    dialogRef.current?.showModal();
  }, [gross]);

  const unausgeliefert = dokumente.filter(
    (dokument) =>
      dokument.currentRevision === null &&
      !zeilen.some((zeile) => zeile.assetId === dokument.id) &&
      !fortsetzbar.some((wunsch) => wunsch.assetId === dokument.id),
  );
  const fotos = dokumente.filter(
    (dokument): dokument is Dokument & { currentRevision: DokumentRevision } =>
      dokument.assetType === "photo" && dokument.currentRevision !== null,
  );
  const dokumentEintraege = dokumente.filter(
    (dokument): dokument is Dokument & { currentRevision: DokumentRevision } =>
      dokument.assetType === "document" && dokument.currentRevision !== null,
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
      assetTyp: dokumentTypFuerDatei(datei),
      beschreibung: "",
      assetId: null,
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
    const verbindung = verbindungsRef.current;
    verbindungsRef.current = null;
    if (verbindung) {
      const zeile: DateiZeile = {
        id: naechsteZeileId.current++,
        datei: liste[0],
        assetTyp: verbindung.assetTyp,
        beschreibung: verbindung.beschreibung,
        assetId: verbindung.assetId,
        status: "warten",
        fortschritt: 0,
        fehler: "",
      };
      zeilenRef.current = [...zeilenRef.current, zeile];
      setZeilen(zeilenRef.current);
      setUebertragLaeuft(true);
      void starteZeile(zeile, null).then(() => {
        setUebertragLaeuft(false);
        batchMeldungNeu(zeilenRef.current);
        aktualisieren();
      });
      return;
    }
    const wunsch = wunschRef.current;
    wunschRef.current = null;
    if (wunsch) {
      void bindeDateiAnMaterial(wunsch, liste[0]);
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

  async function starteZeile(
    zeile: DateiZeile,
    wiederverwendbar: string | null,
  ) {
    const abbruch = new AbortController();
    abbrueche.current.set(zeile.id, abbruch);
    zeileAendern(zeile.id, {
      status: "uebertragen",
      fehler: "",
      fortschritt: 0,
    });
    let assetId = zeile.assetId ?? wiederverwendbar;
    try {
      if (!assetId) {
        const body: { assetType: DokumentTyp; description?: string } = {
          assetType: zeile.assetTyp,
        };
        const beschreibung = zeile.beschreibung.trim();
        if (beschreibung) body.description = beschreibung;
        assetId = (await createEventAsset(auftritt.id, body)).id;
      } else {
        // Bestehendes Material weiterverwenden: die Beschreibung geht mit,
        // solange nichts hochgeladen ist auch die Materialart.
        const body: { assetType?: DokumentTyp; description?: string } = {
          assetType: zeile.assetTyp,
        };
        const beschreibung = zeile.beschreibung.trim();
        if (beschreibung) body.description = beschreibung;
        await patchEventAsset(assetId, body);
      }
      const revision = await uebertrageDatei(
        assetId,
        zeile.datei,
        contentTypeFuerDokument(zeile.assetTyp, zeile.datei),
        (schritt: MaterialSchritt) => {
          zeileAendern(zeile.id, { status: schritt });
        },
        {
          onFortschritt: (uebertragenBytes, gesamtBytes) => {
            zeileAendern(zeile.id, {
              fortschritt: (uebertragenBytes / gesamtBytes) * 100,
            });
          },
          signal: abbruch.signal,
        },
      );
      zeileAendern(zeile.id, {
        status: "gespeichert",
        assetId,
        fehler: "",
      });
      void revision;
    } catch (ursache) {
      const abgebrochen =
        abbruch.signal.aborted || ursache instanceof AbbruchFehler;
      zeileAendern(zeile.id, {
        status: abgebrochen ? "abgebrochen" : "gescheitert",
        assetId,
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

  async function bindeDateiAnMaterial(wunsch: FortsetzungsWunsch, datei: File) {
    const identisch =
      datei.name === wunsch.eintrag.fileName &&
      datei.size === wunsch.eintrag.sizeBytes &&
      datei.lastModified === wunsch.eintrag.lastModified;
    if (!identisch) {
      // Eine andere Datei als die gemerkte: die alte Sitzung wird
      // abgebrochen, der Upload startet mit einer frischen Zeile.
      entferneUploadSitzung(wunsch.assetId);
      // Die ferne Sitzung wird daneben still abgemeldet, damit das
      // gemerkte Größenbudget sofort wieder frei wird (Rezeptur
      // noten-bereich.tsx).
      void cancelUploadSession(wunsch.eintrag.uploadSessionId).catch(() => {});
      const neue: DateiZeile = {
        id: naechsteZeileId.current++,
        datei,
        assetTyp: wunsch.assetTyp,
        beschreibung: "",
        assetId: wunsch.assetId,
        status: "warten",
        fortschritt: 0,
        fehler: "",
      };
      zeilenRef.current = [...zeilenRef.current, neue];
      setZeilen(zeilenRef.current);
      setUebertragLaeuft(true);
      await starteZeile(neue, null);
      setUebertragLaeuft(false);
      batchMeldungNeu(zeilenRef.current);
      aktualisieren();
      return;
    }
    const zeile: DateiZeile = {
      id: naechsteZeileId.current++,
      datei,
      assetTyp: wunsch.assetTyp,
      beschreibung: "",
      assetId: wunsch.assetId,
      status: "uebertragen",
      fortschritt: 0,
      fehler: "",
    };
    zeilenRef.current = [...zeilenRef.current, zeile];
    setZeilen(zeilenRef.current);
    setFortsetzbar((vorher) =>
      vorher.filter((eintrag) => eintrag.assetId !== wunsch.assetId),
    );
    setUebertragLaeuft(true);
    const abbruch = new AbortController();
    abbrueche.current.set(zeile.id, abbruch);
    try {
      const revision = await setzeUploadFort(
        wunsch.assetId,
        datei,
        contentTypeFuerDokument(wunsch.assetTyp, datei),
        (schritt: MaterialSchritt) => {
          zeileAendern(zeile.id, { status: schritt });
        },
        {
          onFortschritt: (uebertragenBytes, gesamtBytes) => {
            zeileAendern(zeile.id, {
              fortschritt: (uebertragenBytes / gesamtBytes) * 100,
            });
          },
          signal: abbruch.signal,
        },
      );
      zeileAendern(zeile.id, { status: "gespeichert", fehler: "" });
      void revision;
    } catch (ursache) {
      const abgebrochen =
        abbruch.signal.aborted || ursache instanceof AbbruchFehler;
      zeileAendern(zeile.id, {
        status: abgebrochen ? "abgebrochen" : "gescheitert",
        fehler: abgebrochen ? "" : await fehlerMeldung(ursache, startMeldung),
      });
    } finally {
      abbrueche.current.delete(zeile.id);
      setUebertragLaeuft(false);
      batchMeldungNeu(zeilenRef.current);
      aktualisieren();
    }
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
    // Bereits angelegtes, noch leeres Material derselben Art wird
    // weiterverwendet: ein gescheiterter Versuch hinterlässt keine
    // Doppel-Einträge.
    const wiederverwendbar: Record<DokumentTyp, string[]> = {
      document: [],
      photo: [],
    };
    for (const dokument of dokumente) {
      if (
        dokument.currentRevision === null &&
        istDokumentTyp(dokument.assetType)
      ) {
        wiederverwendbar[dokument.assetType].push(dokument.id);
      }
    }
    let naechster = 0;
    const arbeiter = async () => {
      while (naechster < abrechnung.length) {
        const zeile = abrechnung[naechster++];
        const erbe = wiederverwendbar[zeile.assetTyp].shift() ?? null;
        await starteZeile(zeile, erbe);
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
    await starteZeile(zeile, null);
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
                  <div className="material-fortsetzung" key={wunsch.assetId}>
                    <span className="material-datei-name">
                      {wunsch.eintrag.fileName}
                    </span>
                    <span className="material-datei-groesse">
                      {groesseText(wunsch.eintrag.sizeBytes)}
                    </span>
                    <button
                      type="button"
                      disabled={uebertragLaeuft}
                      onClick={() => {
                        // Nur ein Wunsch je Mausklick: ein noch bewaffneter
                        // Hochlade-Wunsch aus der anderen Zeile würde die
                        // gewählte Datei an das falsche Material binden.
                        wunschRef.current = wunsch;
                        verbindungsRef.current = null;
                        eingabeRef.current?.click();
                      }}
                    >
                      Fortsetzen
                    </button>
                  </div>
                ))}
              </div>
            )}
            {unausgeliefert.length > 0 && (
              <div className="material-fortsetzungen">
                <p>Noch nicht hochgeladen</p>
                {unausgeliefert.map((dokument) => (
                  <UnausgeliefertZeile
                    dokument={dokument}
                    busy={uebertragLaeuft}
                    onHochladen={(gewaehlt) => {
                      // Nur ein Wunsch je Mausklick: ein noch bewaffneter
                      // Fortsetzungs-Wunsch würde die gewählte Datei auf die
                      // unterbrochene Übertragung umlenken.
                      wunschRef.current = null;
                      verbindungsRef.current = {
                        assetId: gewaehlt.id,
                        assetTyp: gewaehlt.assetType,
                        beschreibung: gewaehlt.description ?? "",
                      };
                      eingabeRef.current?.click();
                    }}
                    onBearbeitet={() => setErfolg("Änderungen gespeichert.")}
                    aktualisieren={aktualisieren}
                    key={dokument.id}
                  />
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
                      <label htmlFor={`dokument-zeile-${zeile.id}-typ`}>
                        Materialtyp
                      </label>
                      <select
                        id={`dokument-zeile-${zeile.id}-typ`}
                        value={zeile.assetTyp}
                        disabled={zeile.status !== "warten"}
                        onChange={(event) =>
                          zeileAendern(zeile.id, {
                            assetTyp: event.target.value as DokumentTyp,
                          })
                        }
                      >
                        <option value="document">Dokument (PDF)</option>
                        <option value="photo">Fotografie</option>
                      </select>
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
                  verbindungsRef.current = null;
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
            {typText(gross.dokument.currentRevision?.contentType ?? "")} ·{" "}
            {groesseText(gross.dokument.currentRevision?.sizeBytes ?? 0)}
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
