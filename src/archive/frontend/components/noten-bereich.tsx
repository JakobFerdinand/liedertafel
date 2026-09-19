"use client";

import { useRef, useState } from "react";
import {
  type AssetAccessResponse,
  type AssetType,
  assetTypFuerDatei,
  contentTypeFuerAssetTyp,
  createAsset,
  fetchAssetAccess,
  MaterialFehler,
  type MaterialSchritt,
  patchAsset,
  problemTitel,
  stimmeAusDateiname,
  stimmenVorschlaege,
  uebertrageDatei,
} from "@/lib/assets";
import type { LiedAsset } from "@/lib/songs";

type MaterialStatus =
  | "warten"
  | "uebertragen"
  | "geprueft"
  | "gespeichert"
  | "gescheitert";

type DateiZeile = {
  id: number;
  datei: File;
  assetTyp: AssetType;
  stimme: string;
  beschreibung: string;
  assetId: string | null;
  status: MaterialStatus;
  fehler: string;
};

type NotenBereichProps = {
  fassungId: string;
  fassungLabel: string;
  stimmenKonfiguration: string | null;
  assets: LiedAsset[];
  isEditor: boolean;
  aktualisieren: () => void;
};

const startMeldung =
  "Das Hochladen konnte nicht gestartet werden. Bitte erneut versuchen.";

const materialgruppen: [AssetType, string][] = [
  ["score", "Noten"],
  ["audio", "Audio"],
  ["midi", "MIDI"],
];

function groesseText(sizeBytes: number): string {
  const format = new Intl.NumberFormat("de-AT", {
    maximumFractionDigits: 1,
  });
  if (sizeBytes >= 102400) return `${format.format(sizeBytes / 1048576)} MB`;
  if (sizeBytes >= 1024) return `${format.format(sizeBytes / 1024)} kB`;
  return `${format.format(sizeBytes)} B`;
}

function typText(contentType: string): string {
  return contentType.includes("pdf") ? "PDF" : contentType;
}

function istAssetTyp(typ: string): typ is AssetType {
  return typ === "score" || typ === "audio" || typ === "midi";
}

function statusText(status: MaterialStatus): string {
  switch (status) {
    case "warten":
      return "wartet";
    case "uebertragen":
      return "wird übertragen …";
    case "geprueft":
      return "wird geprüft …";
    case "gespeichert":
      return "gespeichert.";
    case "gescheitert":
      return "gescheitert";
  }
}

async function fehlerMeldung(
  ursache: unknown,
  ersatz: string,
): Promise<string> {
  if (ursache instanceof MaterialFehler) return ursache.message;
  return problemTitel(ursache, ersatz);
}

export function NotenBereich({
  fassungId,
  fassungLabel,
  stimmenKonfiguration,
  assets,
  isEditor,
  aktualisieren,
}: NotenBereichProps) {
  const eingabeRef = useRef<HTMLInputElement>(null);
  const naechsteZeileId = useRef(0);
  const zeilenRef = useRef<DateiZeile[]>([]);
  const [zeilen, setZeilen] = useState<DateiZeile[]>([]);
  const [uebertragLaeuft, setUebertragLaeuft] = useState(false);
  const [batchMeldung, setBatchMeldung] = useState("");
  const [zugriffe, setZugriffe] = useState<Record<string, AssetAccessResponse>>(
    {},
  );
  const [zugriffBusy, setZugriffBusy] = useState<Record<string, boolean>>({});
  const [zugriffFehler, setZugriffFehler] = useState("");
  const [bearbeitenId, setBearbeitenId] = useState<string | null>(null);
  const [bearbeitenStimme, setBearbeitenStimme] = useState("");
  const [bearbeitenBeschreibung, setBearbeitenBeschreibung] = useState("");
  const [bearbeitenBusy, setBearbeitenBusy] = useState(false);
  const [bearbeitenFehler, setBearbeitenFehler] = useState("");
  const [bearbeitenErfolg, setBearbeitenErfolg] = useState("");

  if (fassungId === "") return null;

  const veroeffentlicht = assets.filter(
    (asset) => asset.currentRevision !== null,
  );
  if (!isEditor && veroeffentlicht.length === 0) return null;

  const vorschlaege = stimmenVorschlaege(stimmenKonfiguration);

  function zeileAendern(id: number, aenderung: Partial<DateiZeile>) {
    const stand = zeilenRef.current.map((zeile) =>
      zeile.id === id ? { ...zeile, ...aenderung } : zeile,
    );
    zeilenRef.current = stand;
    setZeilen(stand);
  }

  function dateienHinzufuegen(liste: FileList | File[]) {
    const vorschlaege = stimmenVorschlaege(stimmenKonfiguration);
    const neue: DateiZeile[] = Array.from(liste).map((datei) => ({
      id: naechsteZeileId.current++,
      datei,
      assetTyp: assetTypFuerDatei(datei),
      stimme: stimmeAusDateiname(datei.name, vorschlaege),
      beschreibung: "",
      assetId: null,
      status: "warten",
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
    zeileAendern(zeile.id, { status: "uebertragen", fehler: "" });
    let assetId = zeile.assetId;
    try {
      if (!assetId) {
        const body: Record<string, string> = { assetType: zeile.assetTyp };
        const stimme = zeile.stimme.trim();
        const beschreibung = zeile.beschreibung.trim();
        if (stimme) body.voiceLabel = stimme;
        if (beschreibung) body.description = beschreibung;
        if (wiederverwendbar) {
          assetId = wiederverwendbar;
          await patchAsset(assetId, {
            voiceLabel: stimme || "",
            description: beschreibung || "",
          });
        } else {
          assetId = (await createAsset(fassungId, body)).id;
        }
      }
      const revision = await uebertrageDatei(
        assetId,
        zeile.datei,
        contentTypeFuerAssetTyp(zeile.assetTyp, zeile.datei),
        (schritt: MaterialSchritt) => {
          zeileAendern(zeile.id, { status: schritt });
        },
      );
      zeileAendern(zeile.id, {
        status: "gespeichert",
        assetId,
        fehler: "",
      });
      void revision;
    } catch (ursache) {
      zeileAendern(zeile.id, {
        status: "gescheitert",
        assetId,
        fehler: await fehlerMeldung(ursache, startMeldung),
      });
    }
  }

  function batchMeldungNeu(stand: DateiZeile[]) {
    const gesamt = stand.length;
    const gesichert = stand.filter(
      (zeile) => zeile.status === "gespeichert",
    ).length;
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
    const wiederverwendbar: Record<AssetType, string[]> = {
      score: [],
      audio: [],
      midi: [],
    };
    for (const asset of assets) {
      if (asset.currentRevision === null && istAssetTyp(asset.assetType)) {
        wiederverwendbar[asset.assetType].push(asset.id);
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

  async function anzeigen(asset: LiedAsset) {
    setZugriffBusy((vorher) => ({ ...vorher, [asset.id]: true }));
    setZugriffFehler("");
    try {
      const zugriff = await fetchAssetAccess(asset.id);
      setZugriffe((vorher) => ({ ...vorher, [asset.id]: zugriff }));
    } catch (ursache) {
      if (ursache instanceof Response && ursache.status === 404) {
        setZugriffFehler(
          "Für dieses Material liegt keine abrufbare Datei vor.",
        );
      } else {
        setZugriffFehler(
          "Material konnte nicht geladen werden. Bitte erneut versuchen.",
        );
      }
    } finally {
      setZugriffBusy((vorher) => ({ ...vorher, [asset.id]: false }));
    }
  }

  function bearbeitenSchliessen() {
    setBearbeitenId(null);
    setBearbeitenFehler("");
    setBearbeitenErfolg("");
  }

  async function bearbeitenSpeichern(asset: LiedAsset) {
    setBearbeitenBusy(true);
    setBearbeitenFehler("");
    try {
      await patchAsset(asset.id, {
        voiceLabel: bearbeitenStimme.trim(),
        description: bearbeitenBeschreibung.trim(),
      });
      bearbeitenSchliessen();
      setBearbeitenErfolg("Änderungen gespeichert.");
      aktualisieren();
    } catch (ursache) {
      setBearbeitenFehler(
        await fehlerMeldung(
          ursache,
          "Das hat nicht geklappt. Bitte erneut versuchen.",
        ),
      );
    } finally {
      setBearbeitenBusy(false);
    }
  }

  const gesamt = zeilen.length;
  const gesichert = zeilen.filter(
    (zeile) => zeile.status === "gespeichert",
  ).length;

  return (
    <section className="noten-bereich materialien" aria-label="Material">
      <h3 id="material-titel">Material</h3>
      <p className="noten-info">Fassung: {fassungLabel}</p>
      {veroeffentlicht.length > 0 && (
        <div className="material-liste">
          {bearbeitenErfolg && (
            <output aria-live="polite" className="auth-erfolg">
              {bearbeitenErfolg}
            </output>
          )}
          {zugriffFehler && (
            <output aria-live="polite" className="feld-fehler">
              {zugriffFehler}
            </output>
          )}
          {materialgruppen.map(([typ, titel]) => {
            const gruppenAssets = veroeffentlicht.filter(
              (asset) => asset.assetType === typ,
            );
            if (gruppenAssets.length === 0) return null;
            return (
              <div className="material-gruppe" key={typ}>
                <h4>{titel}</h4>
                {gruppenAssets.map((asset) => {
                  const revision = asset.currentRevision;
                  if (!revision) return null;
                  const stimme = asset.voiceLabel?.trim() || "Vollmix";
                  const zugriff = zugriffe[asset.id] ?? null;
                  return (
                    <article
                      className="material-eintrag"
                      key={asset.id}
                      aria-label={`${titel} · ${stimme}`}
                    >
                      <p className="material-stimme">{stimme}</p>
                      <p className="noten-info">
                        {titel} · Fassung {revision.revisionNumber} ·{" "}
                        {groesseText(revision.sizeBytes)} ·{" "}
                        {typText(revision.contentType)}
                      </p>
                      {asset.description?.trim() && (
                        <p className="material-beschreibung">
                          {asset.description.trim()}
                        </p>
                      )}
                      <div className="noten-aktionen">
                        {typ === "score" ? (
                          zugriff ? (
                            <iframe
                              className="noten-ansicht"
                              src={zugriff.viewUrl}
                              title={`Noten (PDF) · ${stimme}`}
                            />
                          ) : (
                            <button
                              type="button"
                              onClick={() => void anzeigen(asset)}
                              disabled={zugriffBusy[asset.id] === true}
                            >
                              {zugriffBusy[asset.id]
                                ? "Noten werden vorbereitet …"
                                : "Noten anzeigen"}
                            </button>
                          )
                        ) : null}
                        {typ === "score" && zugriff && (
                          <a
                            className="noten-laden"
                            href={zugriff.downloadUrl}
                            download
                          >
                            Herunterladen
                          </a>
                        )}
                        {typ !== "score" &&
                          (zugriff ? (
                            <a
                              className="noten-laden"
                              href={zugriff.downloadUrl}
                              download
                            >
                              Herunterladen
                            </a>
                          ) : (
                            <button
                              type="button"
                              onClick={() => void anzeigen(asset)}
                              disabled={zugriffBusy[asset.id] === true}
                            >
                              {zugriffBusy[asset.id]
                                ? "Wird vorbereitet …"
                                : "Herunterladen"}
                            </button>
                          ))}
                        {isEditor && (
                          <button
                            type="button"
                            onClick={() =>
                              bearbeitenId === asset.id
                                ? bearbeitenSchliessen()
                                : (() => {
                                    setBearbeitenId(asset.id);
                                    setBearbeitenStimme(asset.voiceLabel ?? "");
                                    setBearbeitenBeschreibung(
                                      asset.description ?? "",
                                    );
                                    setBearbeitenFehler("");
                                  })()
                            }
                          >
                            {bearbeitenId === asset.id
                              ? "Bearbeiten schließen"
                              : "Bearbeiten"}
                          </button>
                        )}
                      </div>
                      {isEditor && bearbeitenId === asset.id && (
                        <div className="material-bearbeiten">
                          <label htmlFor={`material-${asset.id}-stimme`}>
                            Stimme (optional)
                          </label>
                          <input
                            id={`material-${asset.id}-stimme`}
                            type="text"
                            maxLength={200}
                            list="material-stimmen"
                            value={bearbeitenStimme}
                            onChange={(event) =>
                              setBearbeitenStimme(event.target.value)
                            }
                          />
                          <label htmlFor={`material-${asset.id}-beschreibung`}>
                            Beschreibung (optional)
                          </label>
                          <input
                            id={`material-${asset.id}-beschreibung`}
                            type="text"
                            maxLength={500}
                            value={bearbeitenBeschreibung}
                            onChange={(event) =>
                              setBearbeitenBeschreibung(event.target.value)
                            }
                          />
                          {bearbeitenFehler && (
                            <p role="alert" className="feld-fehler">
                              {bearbeitenFehler}
                            </p>
                          )}
                          <div className="auth-aktionen">
                            <button
                              type="button"
                              disabled={bearbeitenBusy}
                              onClick={() => void bearbeitenSpeichern(asset)}
                            >
                              {bearbeitenBusy
                                ? "Wird gespeichert …"
                                : "Änderungen speichern"}
                            </button>
                          </div>
                        </div>
                      )}
                    </article>
                  );
                })}
              </div>
            );
          })}
        </div>
      )}
      {isEditor && (
        <fieldset
          className="material-verwaltung"
          aria-label="Material verwalten"
          onDrop={ablegen}
          onDragOver={ziehStart}
        >
          {batchMeldung && (
            <output aria-live="polite" className="auth-erfolg">
              {batchMeldung}
            </output>
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
                    <span className="material-datei-status" aria-live="polite">
                      {statusText(zeile.status)}
                    </span>
                  </div>
                  {zeile.status === "gescheitert" && (
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
                    <label htmlFor={`material-${zeile.id}-typ`}>
                      Materialtyp
                    </label>
                    <select
                      id={`material-${zeile.id}-typ`}
                      value={zeile.assetTyp}
                      disabled={zeile.status !== "warten"}
                      onChange={(event) =>
                        zeileAendern(zeile.id, {
                          assetTyp: event.target.value as AssetType,
                        })
                      }
                    >
                      <option value="score">Noten (PDF)</option>
                      <option value="audio">Audio</option>
                      <option value="midi">MIDI</option>
                    </select>
                    <label htmlFor={`material-${zeile.id}-stimme`}>
                      Stimme (optional)
                    </label>
                    <input
                      id={`material-${zeile.id}-stimme`}
                      type="text"
                      maxLength={200}
                      list="material-stimmen"
                      value={zeile.stimme}
                      disabled={zeile.status !== "warten"}
                      onChange={(event) =>
                        zeileAendern(zeile.id, { stimme: event.target.value })
                      }
                    />
                    <label htmlFor={`material-${zeile.id}-beschreibung`}>
                      Beschreibung (optional)
                    </label>
                    <input
                      id={`material-${zeile.id}-beschreibung`}
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
          <datalist id="material-stimmen">
            {vorschlaege.map((vorschlag) => (
              <option key={vorschlag} value={vorschlag} />
            ))}
          </datalist>
          <div className="noten-aktionen">
            <button
              type="button"
              onClick={() => eingabeRef.current?.click()}
              disabled={uebertragLaeuft}
            >
              Material hochladen
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
              onChange={eingabeWaehlen}
              disabled={uebertragLaeuft}
              tabIndex={-1}
              aria-hidden="true"
            />
          </div>
        </fieldset>
      )}
    </section>
  );
}
