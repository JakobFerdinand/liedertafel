"use client";

// ARC-030: ganze Aufnahmen eines Auftritts. Mitglieder sehen und hören die
// veröffentlichten Aufnahmen über den gemeinsamen Spieler (Ticket wird still
// erneuert); die Redaktion legt Aufnahmen an, überträgt Original und – wo der
// Browser das Original nicht abspielt – eine umgewandelte Abspielfassung,
// veröffentlicht und gibt das Herunterladen frei. Eine Aufnahme ist Material
// zum Auftritt und nie eine Aufführung; Zeitmarken zu einzelnen Liedern
// folgen getrennt (ARC-032) und sind hier keine Voraussetzung.

import { useCallback, useEffect, useRef, useState } from "react";
import { MedienSpieler } from "@/components/medien-spieler";
import {
  AbbruchFehler,
  cancelUploadSession,
  entferneUploadSitzung,
  fetchAssetAccess,
  liesUploadSitzung,
  MaterialFehler,
  type MaterialSchritt,
  problemTitel,
  type RevisionResponse,
  restlaufzeitMs,
  setzeUploadFort,
  uebertrageDatei,
  zeitText,
} from "@/lib/assets";
import {
  type Aufnahme,
  type AufnahmeArt,
  type AufnahmeDatei,
  AufnahmeVeraltet,
  type AufnahmeZugriff,
  artFuerDatei,
  aufnahmeArtName,
  aufnahmeGroesse,
  createAufnahme,
  fetchAufnahmen,
  fetchAufnahmeZugriff,
  formatName,
  messeDauer,
  oeffneAbspielfassung,
  patchAufnahme,
} from "@/lib/aufnahmen";

/** Ein Ticket wird verworfen, bevor es abläuft – nie danach. */
const VerwerfenVorlaufMs = 5_000;

const LadeErsatz =
  "Die Aufnahme konnte nicht geladen werden. Bitte erneut versuchen.";

const SpeicherErsatz = "Das hat nicht geklappt. Bitte erneut versuchen.";

const UebertragErsatz =
  "Die Übertragung ist gescheitert. Dieselbe Datei erneut wählen, um fortzusetzen.";

type Platz = "original" | "abspielfassung";

/** Ein Ticket, mit dem sich tatsächlich etwas abspielen lässt. */
type SpielZugriff = AufnahmeZugriff & { viewUrl: string };

async function holeSpielZugriff(id: string): Promise<SpielZugriff | null> {
  const zugriff = await fetchAufnahmeZugriff(id);
  return zugriff.viewUrl === null
    ? null
    : { ...zugriff, viewUrl: zugriff.viewUrl };
}

type Uebertragung = {
  status: "uebertragen" | "geprueft" | "abbruchLaeuft" | "gescheitert";
  dateiName: string;
  fortschritt: number;
  fehler: string;
};

type Entwurf = {
  label: string;
  isPublished: boolean;
  downloadEnabled: boolean;
};

function platzSchluessel(aufnahmeId: string, platz: Platz) {
  return `${aufnahmeId}:${platz}`;
}

function platzName(platz: Platz) {
  return platz === "original" ? "Original" : "Abspielfassung";
}

// Entfernte oder zurückgezogene Aufnahmen (404) erklären sich selbst,
// abgelaufene Anmeldungen weisen aufs Neuladen, Vorübergehendes lädt zum
// erneuten Versuch ein.
function zugriffsMeldung(ursache: unknown): string {
  const status = ursache instanceof Response ? ursache.status : 0;
  if (status === 404) return "Die Aufnahme ist nicht mehr verfügbar.";
  if (status === 401) {
    return "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.";
  }
  return LadeErsatz;
}

async function fehlerText(ursache: unknown, ersatz: string): Promise<string> {
  if (ursache instanceof MaterialFehler) return ursache.message;
  return problemTitel(ursache, ersatz);
}

function infoZeile(aufnahme: Aufnahme): string {
  const teile = [aufnahmeArtName(aufnahme.kind)];
  if (aufnahme.playback.contentType) {
    teile.push(formatName(aufnahme.playback.contentType));
  }
  if (aufnahme.playback.sizeBytes !== null) {
    teile.push(aufnahmeGroesse(aufnahme.playback.sizeBytes));
  }
  if (aufnahme.durationSeconds !== null) {
    teile.push(`Dauer ${zeitText(aufnahme.durationSeconds)}`);
  }
  return teile.join(" · ");
}

function dateiZeile(datei: AufnahmeDatei): string {
  return [
    datei.fileName ?? "ohne Dateinamen",
    formatName(datei.contentType),
    aufnahmeGroesse(datei.sizeBytes),
    `Dateistand ${datei.revisionNumber}`,
  ].join(" · ");
}

export function AuftrittAufnahmen({
  auftrittId,
  isEditor,
  startAufnahmeId,
  startSekunden,
}: {
  auftrittId: string;
  isEditor: boolean;
  /** Verweis auf eine bestimmte Aufnahme (?aufnahme=…), optional mit Zeit (&t=…). */
  startAufnahmeId?: string | null;
  startSekunden?: number | null;
}) {
  const [aufnahmen, setAufnahmen] = useState<Aufnahme[] | null>(null);
  const [ladeFehler, setLadeFehler] = useState("");
  const [zugriffe, setZugriffe] = useState<Record<string, SpielZugriff>>({});
  const [zugriffFehler, setZugriffFehler] = useState<Record<string, string>>(
    {},
  );
  const [beschaeftigt, setBeschaeftigt] = useState("");
  const [spielend, setSpielend] = useState<string | null>(null);
  const [meldung, setMeldung] = useState("");
  const [uebertragungen, setUebertragungen] = useState<
    Record<string, Uebertragung>
  >({});
  const abbrueche = useRef(new Map<string, AbortController>());
  const startErledigt = useRef(false);
  const titelRefs = useRef(new Map<string, HTMLHeadingElement>());
  const [fokusZiel, setFokusZiel] = useState<string | null>(null);

  const laden = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const liste = await fetchAufnahmen(auftrittId, signal);
        if (signal?.aborted) return null;
        setAufnahmen(liste);
        setLadeFehler("");
        return liste;
      } catch (ursache) {
        if (signal?.aborted) return null;
        const status = ursache instanceof Response ? ursache.status : 0;
        setLadeFehler(
          status === 401
            ? "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu."
            : "Die Aufnahmen konnten nicht geladen werden.",
        );
        return null;
      }
    },
    [auftrittId],
  );

  useEffect(() => {
    const abbruch = new AbortController();
    void laden(abbruch.signal);
    return () => abbruch.abort();
  }, [laden]);

  const schliessen = useCallback((id: string) => {
    setZugriffe((vorher) => {
      const { [id]: _weg, ...rest } = vorher;
      return rest;
    });
    setSpielend((vorher) => (vorher === id ? null : vorher));
  }, []);

  // Kein Ticket überlebt seinen Ablauf im Speicher der Seite: der Spieler
  // erneuert vorher; gelingt das nicht, wird das Ticket rechtzeitig
  // verworfen und der Eintrag sagt, warum die Wiedergabe endete.
  useEffect(() => {
    const eintraege = Object.entries(zugriffe);
    if (eintraege.length === 0) return;
    const naechster = Math.min(
      ...eintraege.map(([, zugriff]) => restlaufzeitMs(zugriff.expiresAt)),
    );
    const timer = window.setTimeout(
      () => {
        for (const [id, zugriff] of eintraege) {
          if (restlaufzeitMs(zugriff.expiresAt) > VerwerfenVorlaufMs) continue;
          schliessen(id);
          setZugriffFehler((vorher) => ({
            ...vorher,
            [id]: "Der Zugriff auf die Aufnahme ist abgelaufen und konnte nicht erneuert werden. Bitte erneut öffnen.",
          }));
        }
      },
      Math.max(0, naechster - VerwerfenVorlaufMs),
    );
    return () => window.clearTimeout(timer);
  }, [zugriffe, schliessen]);

  const oeffnen = useCallback(async (id: string) => {
    setBeschaeftigt(`oeffnen:${id}`);
    setZugriffFehler((vorher) => ({ ...vorher, [id]: "" }));
    try {
      const zugriff = await holeSpielZugriff(id);
      if (zugriff === null) {
        setZugriffFehler((vorher) => ({
          ...vorher,
          [id]: "Für diese Aufnahme liegt noch keine im Browser abspielbare Fassung vor.",
        }));
        return;
      }
      setZugriffe((vorher) => ({ ...vorher, [id]: zugriff }));
    } catch (ursache) {
      setZugriffFehler((vorher) => ({
        ...vorher,
        [id]: zugriffsMeldung(ursache),
      }));
    } finally {
      setBeschaeftigt("");
    }
  }, []);

  // Verweis auf eine bestimmte Aufnahme: einmal öffnen, nicht von selbst
  // abspielen; die Zeitangabe stellt der Spieler ein.
  useEffect(() => {
    if (startErledigt.current || !startAufnahmeId || aufnahmen === null) {
      return;
    }
    startErledigt.current = true;
    const ziel = aufnahmen.find((aufnahme) => aufnahme.id === startAufnahmeId);
    if (!ziel) return;
    setFokusZiel(ziel.id);
    if (ziel.playback.state === "ready") void oeffnen(ziel.id);
  }, [aufnahmen, startAufnahmeId, oeffnen]);

  useEffect(() => {
    if (!fokusZiel) return;
    const titel = titelRefs.current.get(fokusZiel);
    if (!titel) return;
    titel.focus();
    titel.scrollIntoView({ block: "start" });
    setFokusZiel(null);
  }, [fokusZiel]);

  // Herunterladen holt sein Ticket im Augenblick des Klicks; es wird nirgends
  // aufbewahrt. Die API entscheidet, ob es überhaupt eines gibt.
  async function herunterladen(aufnahme: Aufnahme) {
    setBeschaeftigt(`laden:${aufnahme.id}`);
    setZugriffFehler((vorher) => ({ ...vorher, [aufnahme.id]: "" }));
    try {
      const zugriff = await fetchAufnahmeZugriff(aufnahme.id);
      if (zugriff.downloadUrl === null) {
        setZugriffFehler((vorher) => ({
          ...vorher,
          [aufnahme.id]:
            "Das Herunterladen ist für diese Aufnahme nicht freigegeben.",
        }));
        void laden();
        return;
      }
      window.location.assign(zugriff.downloadUrl);
    } catch (ursache) {
      setZugriffFehler((vorher) => ({
        ...vorher,
        [aufnahme.id]: zugriffsMeldung(ursache),
      }));
    } finally {
      setBeschaeftigt("");
    }
  }

  function uebertragungAendern(
    schluessel: string,
    aenderung: Partial<Uebertragung> | null,
  ) {
    setUebertragungen((vorher) => {
      if (aenderung === null) {
        const { [schluessel]: _weg, ...rest } = vorher;
        return rest;
      }
      const alt = vorher[schluessel] ?? {
        status: "uebertragen" as const,
        dateiName: "",
        fortschritt: 0,
        fehler: "",
      };
      return { ...vorher, [schluessel]: { ...alt, ...aenderung } };
    });
  }

  // Überträgt eine Datei in einen Platz der Aufnahme. Eine gemerkte,
  // unterbrochene Übertragung derselben Datei wird fortgesetzt; reisst die
  // Verbindung oder läuft das Upload-Ticket während einer langen Übertragung
  // ab, wird mit erneuertem Ticket ab dem letzten gesicherten Block
  // weitergemacht, solange dabei Fortschritt entsteht.
  async function uebertrage(
    assetId: string,
    datei: File,
    schluessel: string,
    signal: AbortSignal,
  ): Promise<RevisionResponse> {
    const inhaltstyp = datei.type || "application/octet-stream";
    const gemerkt = liesUploadSitzung(assetId);
    let fortsetzen =
      gemerkt !== null &&
      gemerkt.fileName === datei.name &&
      gemerkt.sizeBytes === datei.size &&
      gemerkt.lastModified === datei.lastModified;
    if (gemerkt !== null && !fortsetzen) {
      entferneUploadSitzung(assetId);
      void cancelUploadSession(gemerkt.uploadSessionId).catch(() => {});
    }
    let gesichert = 0;
    let letzterStand = 0;
    const optionen = {
      onFortschritt: (uebertragen: number, gesamt: number) => {
        gesichert = uebertragen;
        uebertragungAendern(schluessel, {
          fortschritt: gesamt > 0 ? (uebertragen / gesamt) * 100 : 0,
        });
      },
      signal,
    };
    const beiSchritt = (schritt: MaterialSchritt) =>
      uebertragungAendern(schluessel, { status: schritt });
    for (;;) {
      try {
        return fortsetzen
          ? await setzeUploadFort(
              assetId,
              datei,
              inhaltstyp,
              beiSchritt,
              optionen,
            )
          : await uebertrageDatei(
              assetId,
              datei,
              inhaltstyp,
              beiSchritt,
              optionen,
            );
      } catch (ursache) {
        const unterbrochen =
          ursache instanceof MaterialFehler &&
          !(ursache instanceof AbbruchFehler) &&
          ursache.status === null &&
          !signal.aborted &&
          liesUploadSitzung(assetId) !== null &&
          gesichert > letzterStand;
        if (!unterbrochen) throw ursache;
        letzterStand = gesichert;
        fortsetzen = true;
      }
    }
  }

  async function hochladen(aufnahme: Aufnahme, platz: Platz, datei: File) {
    if (!aufnahme.editor) return;
    const schluessel = platzSchluessel(aufnahme.id, platz);
    const abbruch = new AbortController();
    abbrueche.current.set(schluessel, abbruch);
    setMeldung("");
    uebertragungAendern(schluessel, {
      status: "uebertragen",
      dateiName: datei.name,
      fortschritt: 0,
      fehler: "",
    });
    try {
      const assetId =
        platz === "original"
          ? aufnahme.editor.original.assetId
          : (aufnahme.editor.playbackCopy?.assetId ??
            (await oeffneAbspielfassung(aufnahme.id)).editor?.playbackCopy
              ?.assetId);
      if (!assetId) throw new MaterialFehler(SpeicherErsatz);
      const revision = await uebertrage(
        assetId,
        datei,
        schluessel,
        abbruch.signal,
      );
      // Ist die neue Datei das, was Mitglieder abspielen, misst der Browser
      // ihre Länge aus der lokalen Datei und meldet sie dem Archiv.
      const liste = await laden();
      const neu = liste?.find((eintrag) => eintrag.id === aufnahme.id);
      if (neu && neu.playback.revisionId === revision.revisionId) {
        const dauer = await messeDauer(datei, neu.kind);
        if (dauer !== null) {
          await patchAufnahme(neu.id, { durationSeconds: dauer }).catch(
            () => {},
          );
          await laden();
        }
      }
      uebertragungAendern(schluessel, null);
      setMeldung(
        `${platzName(platz)} für „${aufnahme.label}“ gespeichert${
          neu?.playback.state === "needsPlaybackCopy"
            ? ". Die Datei bleibt erhalten, ist aber im Browser nicht abspielbar; bitte eine Abspielfassung ergänzen."
            : "."
        }`,
      );
    } catch (ursache) {
      const abgebrochen =
        abbruch.signal.aborted || ursache instanceof AbbruchFehler;
      if (abgebrochen) {
        uebertragungAendern(schluessel, null);
        setMeldung(`Übertragung für „${aufnahme.label}“ abgebrochen.`);
      } else {
        uebertragungAendern(schluessel, {
          status: "gescheitert",
          fehler: await fehlerText(ursache, UebertragErsatz),
        });
      }
      void laden();
    } finally {
      abbrueche.current.delete(schluessel);
    }
  }

  function abbrechen(schluessel: string) {
    const abbruch = abbrueche.current.get(schluessel);
    if (!abbruch) return;
    uebertragungAendern(schluessel, { status: "abbruchLaeuft" });
    abbruch.abort();
  }

  function uebernehmen(gespeichert: Aufnahme) {
    setAufnahmen(
      (vorher) =>
        vorher?.map((eintrag) =>
          eintrag.id === gespeichert.id ? gespeichert : eintrag,
        ) ?? vorher,
    );
  }

  async function angelegt(neu: Aufnahme, datei: File | null) {
    setMeldung(`Aufnahme „${neu.label}“ angelegt.`);
    await laden();
    setFokusZiel(neu.id);
    if (datei) void hochladen(neu, "original", datei);
  }

  return (
    <section
      className="auftritt-abschnitt aufnahmen"
      aria-labelledby="auftritt-aufnahmen-titel"
    >
      <h3 id="auftritt-aufnahmen-titel">Aufnahmen</h3>
      <output className="aufnahmen-meldung">
        {meldung && <span className="auth-erfolg">{meldung}</span>}
      </output>
      {ladeFehler ? (
        <div role="alert">
          <p className="feld-fehler">{ladeFehler}</p>
          <div className="noten-aktionen">
            <button type="button" onClick={() => void laden()}>
              Aufnahmen erneut laden
            </button>
          </div>
        </div>
      ) : aufnahmen === null ? (
        <p className="auftritt-leer">Aufnahmen werden geladen …</p>
      ) : aufnahmen.length === 0 ? (
        <p className="auftritt-leer">
          Zu diesem Auftritt sind noch keine Aufnahmen hinterlegt.
        </p>
      ) : (
        <div className="material-liste aufnahmen-liste">
          {aufnahmen.map((aufnahme) => {
            const zugriff = zugriffe[aufnahme.id];
            const titelId = `aufnahme-titel-${aufnahme.id}`;
            return (
              <article
                className="material-eintrag aufnahme"
                key={aufnahme.id}
                aria-labelledby={titelId}
              >
                <h4
                  id={titelId}
                  tabIndex={-1}
                  ref={(element) => {
                    if (element) titelRefs.current.set(aufnahme.id, element);
                    else titelRefs.current.delete(aufnahme.id);
                  }}
                >
                  {aufnahme.label}
                </h4>
                {isEditor && (
                  <p className="lieder-status">
                    {aufnahme.isPublished ? "Veröffentlicht" : "Entwurf"}
                    {" · "}
                    {aufnahme.downloadEnabled
                      ? "Herunterladen erlaubt"
                      : "Herunterladen gesperrt"}
                  </p>
                )}
                <p className="noten-info">{infoZeile(aufnahme)}</p>
                {aufnahme.playback.state === "needsPlaybackCopy" && (
                  <p className="aufnahme-zustand">
                    Diese Aufnahme liegt im Archiv, kann aber noch nicht im
                    Browser abgespielt werden.
                    {aufnahme.downloadEnabled
                      ? " Die Originaldatei kann heruntergeladen werden."
                      : ""}
                  </p>
                )}
                {aufnahme.playback.state === "missing" && (
                  <p className="aufnahme-zustand">
                    Zu dieser Aufnahme wurde noch keine Datei hochgeladen.
                  </p>
                )}
                <div className="noten-aktionen">
                  {aufnahme.playback.state === "ready" && (
                    <button
                      type="button"
                      aria-expanded={zugriff !== undefined}
                      aria-label={
                        zugriff
                          ? `${aufnahme.label} schließen`
                          : aufnahme.kind === "video"
                            ? `${aufnahme.label} ansehen`
                            : `${aufnahme.label} anhören`
                      }
                      // Nicht `disabled`: der Knopf behält den Fokus, während
                      // das Ticket geholt wird.
                      aria-disabled={beschaeftigt === `oeffnen:${aufnahme.id}`}
                      onClick={() => {
                        if (beschaeftigt !== "") return;
                        if (zugriff) schliessen(aufnahme.id);
                        else void oeffnen(aufnahme.id);
                      }}
                    >
                      {beschaeftigt === `oeffnen:${aufnahme.id}`
                        ? "Wird geöffnet …"
                        : zugriff
                          ? "Schließen"
                          : aufnahme.kind === "video"
                            ? "Ansehen"
                            : "Anhören"}
                    </button>
                  )}
                  {aufnahme.downloadEnabled &&
                    aufnahme.playback.state !== "missing" && (
                      <button
                        type="button"
                        className="knopf-leise"
                        aria-label={`${aufnahme.label} herunterladen`}
                        aria-disabled={beschaeftigt === `laden:${aufnahme.id}`}
                        onClick={() => {
                          if (beschaeftigt === "") void herunterladen(aufnahme);
                        }}
                      >
                        Herunterladen
                      </button>
                    )}
                </div>
                <div role="alert">
                  {zugriffFehler[aufnahme.id] && (
                    <p className="feld-fehler">{zugriffFehler[aufnahme.id]}</p>
                  )}
                </div>
                {zugriff && (
                  <MedienSpieler
                    kennung={aufnahme.id}
                    art={aufnahme.kind}
                    name={aufnahme.label}
                    zugriff={zugriff}
                    holeZugriff={async () => {
                      const neu = await holeSpielZugriff(aufnahme.id);
                      // Ohne abspielbare Datei gibt es nichts zu erneuern.
                      if (neu === null) {
                        throw new Response(null, { status: 404 });
                      }
                      return neu;
                    }}
                    aktiv={spielend === aufnahme.id}
                    onAbspielen={() => setSpielend(aufnahme.id)}
                    onErneuert={(neu) =>
                      setZugriffe((vorher) =>
                        vorher[aufnahme.id]
                          ? { ...vorher, [aufnahme.id]: neu }
                          : vorher,
                      )
                    }
                    ladeMeldung={LadeErsatz}
                    formatMeldung={
                      aufnahme.downloadEnabled
                        ? "Diese Aufnahme kann in diesem Browser nicht wiedergegeben werden. Sie kann weiterhin heruntergeladen werden."
                        : "Diese Aufnahme kann in diesem Browser nicht wiedergegeben werden."
                    }
                    sprung={
                      aufnahme.id === startAufnahmeId &&
                      startSekunden !== null &&
                      startSekunden !== undefined
                        ? { sekunden: startSekunden, marke: 0 }
                        : undefined
                    }
                  />
                )}
                {isEditor && aufnahme.editor && (
                  <AufnahmeRedaktion
                    aufnahme={aufnahme}
                    uebertragungen={uebertragungen}
                    onGespeichert={(gespeichert, text) => {
                      uebernehmen(gespeichert);
                      setMeldung(text);
                    }}
                    onVeraltet={() => void laden()}
                    onDatei={(platz, datei) =>
                      void hochladen(aufnahme, platz, datei)
                    }
                    onAbbrechen={abbrechen}
                  />
                )}
              </article>
            );
          })}
        </div>
      )}
      {isEditor && aufnahmen !== null && !ladeFehler && (
        <NeueAufnahme auftrittId={auftrittId} onAngelegt={angelegt} />
      )}
    </section>
  );
}

function AufnahmeRedaktion({
  aufnahme,
  uebertragungen,
  onGespeichert,
  onVeraltet,
  onDatei,
  onAbbrechen,
}: {
  aufnahme: Aufnahme;
  uebertragungen: Record<string, Uebertragung>;
  onGespeichert: (aufnahme: Aufnahme, meldung: string) => void;
  onVeraltet: () => void;
  onDatei: (platz: Platz, datei: File) => void;
  onAbbrechen: (schluessel: string) => void;
}) {
  const redaktion = aufnahme.editor;
  // null: das Formular zeigt den Stand des Archivs. Eigene Eingaben bleiben
  // stehen, bis sie gespeichert sind oder ein veralteter Stand sie verwirft.
  const [entwurf, setEntwurf] = useState<Entwurf | null>(null);
  const [fehler, setFehler] = useState("");
  const [speichert, setSpeichert] = useState(false);
  const [ladeFehler, setLadeFehler] = useState("");
  const fehlerRef = useRef<HTMLParagraphElement | null>(null);
  const fokusFehler = useRef(false);

  useEffect(() => {
    if (fehler && fokusFehler.current) {
      fokusFehler.current = false;
      fehlerRef.current?.focus();
    }
  }, [fehler]);

  if (!redaktion) return null;
  const werte: Entwurf = entwurf ?? {
    label: aufnahme.label,
    isPublished: aufnahme.isPublished,
    downloadEnabled: aufnahme.downloadEnabled,
  };
  const geaendert =
    werte.label.trim() !== aufnahme.label ||
    werte.isPublished !== aufnahme.isPublished ||
    werte.downloadEnabled !== aufnahme.downloadEnabled;

  async function speichern(ereignis: React.FormEvent) {
    ereignis.preventDefault();
    if (!redaktion || speichert || !geaendert) return;
    setFehler("");
    setSpeichert(true);
    try {
      const gespeichert = await patchAufnahme(aufnahme.id, {
        ...(werte.label.trim() !== aufnahme.label
          ? { label: werte.label }
          : {}),
        ...(werte.isPublished !== aufnahme.isPublished
          ? { isPublished: werte.isPublished }
          : {}),
        ...(werte.downloadEnabled !== aufnahme.downloadEnabled
          ? { downloadEnabled: werte.downloadEnabled }
          : {}),
        expectedVersion: redaktion.version,
      });
      setEntwurf(null);
      onGespeichert(
        gespeichert,
        `Aufnahme „${gespeichert.label}“ gespeichert.`,
      );
    } catch (ursache) {
      const titel = await problemTitel(ursache, SpeicherErsatz);
      fokusFehler.current = true;
      if (
        ursache instanceof Response &&
        ursache.status === 409 &&
        titel === AufnahmeVeraltet
      ) {
        // Veralteter Stand: neu laden und die Eingabe verwerfen – sie
        // bezog sich auf etwas, das es so nicht mehr gibt.
        setEntwurf(null);
        setFehler(
          `${titel} Der aktuelle Stand wurde geladen; bitte die Änderung erneut vornehmen.`,
        );
        onVeraltet();
        return;
      }
      if (ursache instanceof Response && ursache.status === 404) {
        setFehler("Die Aufnahme ist nicht mehr vorhanden.");
        onVeraltet();
        return;
      }
      // In diesem Zustand nicht erlaubt oder ungültig: die Eingabe bleibt.
      setFehler(titel);
    } finally {
      setSpeichert(false);
    }
  }

  // Das Original für die Umwandlung ausserhalb des Archivs: Ticket im
  // Augenblick des Klicks, nirgends aufbewahrt.
  async function originalLaden(assetId: string) {
    setLadeFehler("");
    try {
      const zugriff = await fetchAssetAccess(assetId);
      window.location.assign(zugriff.downloadUrl);
    } catch (ursache) {
      setLadeFehler(
        await problemTitel(
          ursache,
          "Das Original konnte nicht geladen werden. Bitte erneut versuchen.",
        ),
      );
    }
  }

  const original = redaktion.original.file;
  const abspiel = redaktion.playbackCopy?.file ?? null;

  return (
    <fieldset className="aufnahme-redaktion">
      <legend>Redaktion · {aufnahme.label}</legend>
      <form className="aufnahme-formular" onSubmit={speichern}>
        <div className="material-felder">
          <div>
            <label htmlFor={`aufnahme-label-${aufnahme.id}`}>Bezeichnung</label>
            <input
              id={`aufnahme-label-${aufnahme.id}`}
              type="text"
              maxLength={200}
              required
              value={werte.label}
              onChange={(ereignis) =>
                setEntwurf({ ...werte, label: ereignis.target.value })
              }
            />
          </div>
        </div>
        <label className="aufnahme-schalter">
          <input
            type="checkbox"
            checked={werte.isPublished}
            onChange={(ereignis) =>
              setEntwurf({ ...werte, isPublished: ereignis.target.checked })
            }
          />
          Für Mitglieder veröffentlichen
        </label>
        <label className="aufnahme-schalter">
          <input
            type="checkbox"
            checked={werte.downloadEnabled}
            onChange={(ereignis) =>
              setEntwurf({
                ...werte,
                downloadEnabled: ereignis.target.checked,
              })
            }
          />
          Herunterladen für Mitglieder erlauben
        </label>
        <div className="noten-aktionen">
          {/* Nicht `disabled`: der Knopf behält nach dem Speichern den Fokus. */}
          <button
            type="submit"
            aria-disabled={speichert || !geaendert}
            aria-label={`${aufnahme.label} speichern`}
          >
            {speichert ? "Wird gespeichert …" : "Speichern"}
          </button>
        </div>
        <div role="alert">
          {fehler && (
            <p className="feld-fehler" tabIndex={-1} ref={fehlerRef}>
              {fehler}
            </p>
          )}
        </div>
      </form>

      <div className="aufnahme-dateien">
        <DateiPlatz
          aufnahme={aufnahme}
          platz="original"
          datei={original}
          leerText="Noch nicht hochgeladen."
          zusatz={
            original
              ? original.playable
                ? abspiel
                  ? "Im Browser abspielbar; Mitglieder hören die Abspielfassung."
                  : "Im Browser abspielbar: dient zugleich als Abspielfassung."
                : "Bleibt als Original erhalten, ist aber im Browser nicht abspielbar."
              : ""
          }
          uebertragung={
            uebertragungen[platzSchluessel(aufnahme.id, "original")]
          }
          darfAendern={redaktion.canChangeFiles}
          onDatei={onDatei}
          onAbbrechen={onAbbrechen}
        />
        {original && (
          <div className="noten-aktionen">
            <button
              type="button"
              className="knopf-leise"
              aria-label={`Original von ${aufnahme.label} herunterladen`}
              onClick={() => void originalLaden(redaktion.original.assetId)}
            >
              Original herunterladen
            </button>
          </div>
        )}
        <div role="alert">
          {ladeFehler && <p className="feld-fehler">{ladeFehler}</p>}
        </div>
        <DateiPlatz
          aufnahme={aufnahme}
          platz="abspielfassung"
          datei={abspiel}
          leerText={
            original && !original.playable
              ? "Fehlt. Bitte eine ausserhalb des Archivs umgewandelte Fassung hochladen (MP4, WebM, MP3, M4A oder WAV, passend zur Art der Aufnahme)."
              : "Nicht nötig, solange das Original im Browser abspielbar ist. Spielt es bei Mitgliedern nicht, hier eine umgewandelte Fassung ergänzen."
          }
          zusatz={
            abspiel
              ? "Geprüft und für Mitglieder im Einsatz. Eine neue Datei ersetzt sie erst nach bestandener Prüfung."
              : ""
          }
          uebertragung={
            uebertragungen[platzSchluessel(aufnahme.id, "abspielfassung")]
          }
          darfAendern={redaktion.canChangeFiles}
          onDatei={onDatei}
          onAbbrechen={onAbbrechen}
        />
        {!redaktion.canChangeFiles && (
          <p className="noten-info">
            Dateien kann nur die Person ändern, die die Aufnahme angelegt hat.
          </p>
        )}
        {aufnahme.playback.state === "ready" &&
          aufnahme.durationSeconds === null && (
            <p className="noten-info">Dauer noch nicht ermittelt.</p>
          )}
      </div>
    </fieldset>
  );
}

function DateiPlatz({
  aufnahme,
  platz,
  datei,
  leerText,
  zusatz,
  uebertragung,
  darfAendern,
  onDatei,
  onAbbrechen,
}: {
  aufnahme: Aufnahme;
  platz: Platz;
  datei: AufnahmeDatei | null;
  leerText: string;
  zusatz: string;
  uebertragung: Uebertragung | undefined;
  darfAendern: boolean;
  onDatei: (platz: Platz, datei: File) => void;
  onAbbrechen: (schluessel: string) => void;
}) {
  const eingabeRef = useRef<HTMLInputElement | null>(null);
  const schluessel = platzSchluessel(aufnahme.id, platz);
  const laeuft =
    uebertragung !== undefined && uebertragung.status !== "gescheitert";
  const name = platzName(platz);
  const assetId =
    platz === "original"
      ? aufnahme.editor?.original.assetId
      : aufnahme.editor?.playbackCopy?.assetId;
  // Nach dem Neuladen der Seite: eine gemerkte, unterbrochene Übertragung.
  const [gemerkt, setGemerkt] = useState<string | null>(null);
  useEffect(() => {
    if (!assetId || laeuft) {
      setGemerkt(null);
      return;
    }
    setGemerkt(liesUploadSitzung(assetId)?.fileName ?? null);
  }, [assetId, laeuft]);

  return (
    <div className="aufnahme-platz" data-platz={platz}>
      <p className="aufnahme-platz-titel">{name}</p>
      <p className="noten-info">{datei ? dateiZeile(datei) : leerText}</p>
      {zusatz && <p className="noten-info">{zusatz}</p>}
      {gemerkt && (
        <p className="noten-info">
          Die Übertragung von „{gemerkt}“ wurde unterbrochen. Dieselbe Datei
          erneut wählen, um sie fortzusetzen.
        </p>
      )}
      <output className="material-datei-status aufnahme-uebertragung">
        {uebertragung &&
          (uebertragung.status === "uebertragen"
            ? `${uebertragung.dateiName}: wird übertragen · ${Math.round(uebertragung.fortschritt)} %`
            : uebertragung.status === "geprueft"
              ? `${uebertragung.dateiName}: wird geprüft …`
              : uebertragung.status === "abbruchLaeuft"
                ? `${uebertragung.dateiName}: wird abgebrochen …`
                : `${uebertragung.dateiName}: gescheitert`)}
      </output>
      {laeuft && (
        <div className="material-fortschritt" aria-hidden="true">
          <span
            style={{
              width: `${Math.max(0, Math.min(100, uebertragung?.fortschritt ?? 0))}%`,
            }}
          />
        </div>
      )}
      <div role="alert">
        {uebertragung?.status === "gescheitert" && (
          <p className="feld-fehler">{uebertragung.fehler}</p>
        )}
      </div>
      {darfAendern && (
        <div className="noten-aktionen">
          <input
            ref={eingabeRef}
            type="file"
            className="visually-hidden"
            tabIndex={-1}
            aria-hidden="true"
            data-testid={`aufnahme-datei-${platz}-${aufnahme.id}`}
            accept={
              platz === "abspielfassung"
                ? aufnahme.kind === "video"
                  ? "video/mp4,video/webm,.mp4,.m4v,.webm"
                  : "audio/mpeg,audio/mp4,audio/wav,audio/webm,video/mp4,.mp3,.m4a,.mp4,.wav,.webm"
                : undefined
            }
            onChange={(ereignis) => {
              const gewaehlt = ereignis.target.files?.[0];
              ereignis.target.value = "";
              if (gewaehlt) onDatei(platz, gewaehlt);
            }}
          />
          {laeuft ? (
            <button
              type="button"
              className="knopf-leise"
              aria-label={`Übertragung ${name} für ${aufnahme.label} abbrechen`}
              disabled={uebertragung?.status === "abbruchLaeuft"}
              onClick={() => onAbbrechen(schluessel)}
            >
              Abbrechen
            </button>
          ) : (
            <button
              type="button"
              aria-label={`${name} für ${aufnahme.label} ${datei ? "ersetzen" : "hochladen"}`}
              onClick={() => eingabeRef.current?.click()}
            >
              {name} {datei ? "ersetzen" : "hochladen"}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

function NeueAufnahme({
  auftrittId,
  onAngelegt,
}: {
  auftrittId: string;
  onAngelegt: (aufnahme: Aufnahme, datei: File | null) => Promise<void>;
}) {
  const [label, setLabel] = useState("");
  const [art, setArt] = useState<AufnahmeArt>("video");
  const [artGewaehlt, setArtGewaehlt] = useState(false);
  const [datei, setDatei] = useState<File | null>(null);
  const [fehler, setFehler] = useState("");
  const [busy, setBusy] = useState(false);
  const fehlerRef = useRef<HTMLParagraphElement | null>(null);
  const dateiRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (fehler) fehlerRef.current?.focus();
  }, [fehler]);

  async function anlegen(ereignis: React.FormEvent) {
    ereignis.preventDefault();
    setFehler("");
    setBusy(true);
    try {
      const neu = await createAufnahme(auftrittId, { label, kind: art });
      const gewaehlt = datei;
      setLabel("");
      setDatei(null);
      setArtGewaehlt(false);
      if (dateiRef.current) dateiRef.current.value = "";
      await onAngelegt(neu, gewaehlt);
    } catch (ursache) {
      // Die Eingabe bleibt stehen; nur die Meldung kommt dazu.
      setFehler(await problemTitel(ursache, SpeicherErsatz));
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="material-verwaltung">
      <summary>
        <h2 id="aufnahme-anlegen-titel">Aufnahme hinzufügen</h2>
        <span className="material-verwaltung-umschalter" aria-hidden="true">
          <span className="material-verwaltung-auf">Ausklappen</span>
          <span className="material-verwaltung-zu">Einklappen</span>
        </span>
      </summary>
      <form
        className="aufnahme-formular"
        aria-labelledby="aufnahme-anlegen-titel"
        onSubmit={anlegen}
      >
        <p className="noten-info">
          Eine Aufnahme ist Material zum Auftritt. Sie legt keine Aufführung an
          und ändert weder Programm noch Liedhistorie.
        </p>
        <div className="material-felder">
          <div>
            <label htmlFor="aufnahme-neu-label">Bezeichnung</label>
            <input
              id="aufnahme-neu-label"
              type="text"
              maxLength={200}
              required
              value={label}
              onChange={(ereignis) => setLabel(ereignis.target.value)}
            />
          </div>
          <div>
            <label htmlFor="aufnahme-neu-datei">Datei (optional)</label>
            <input
              id="aufnahme-neu-datei"
              ref={dateiRef}
              type="file"
              onChange={(ereignis) => {
                const gewaehlt = ereignis.target.files?.[0] ?? null;
                setDatei(gewaehlt);
                if (gewaehlt && !artGewaehlt) setArt(artFuerDatei(gewaehlt));
              }}
            />
          </div>
        </div>
        <fieldset className="aufnahme-art">
          <legend>Art der Aufnahme</legend>
          {(["video", "audio"] as const).map((wahl) => (
            <label className="aufnahme-schalter" key={wahl}>
              <input
                type="radio"
                name="aufnahme-neu-art"
                value={wahl}
                checked={art === wahl}
                onChange={() => {
                  setArt(wahl);
                  setArtGewaehlt(true);
                }}
              />
              {aufnahmeArtName(wahl)}
            </label>
          ))}
        </fieldset>
        <div className="noten-aktionen">
          <button type="submit" disabled={busy}>
            {busy ? "Wird angelegt …" : "Aufnahme anlegen"}
          </button>
        </div>
        <div role="alert">
          {fehler && (
            <p className="feld-fehler" tabIndex={-1} ref={fehlerRef}>
              {fehler}
            </p>
          )}
        </div>
      </form>
    </details>
  );
}
