import { deleteAuth, patchAuth, postAuth } from "@/lib/auth";
import type { LiedAsset, LiedRevision } from "@/lib/songs";

export type AssetType = "score" | "audio" | "midi";

export type AssetResponse = LiedAsset & {
  musicalVersionId: string;
  createdAt: string;
};

export type AssetMetadata = {
  assetType?: AssetType;
  voiceLabel?: string | null;
  description?: string | null;
};

export type UploadSessionResponse = {
  uploadSessionId: string;
  blobName: string | null;
  uploadUrl: string;
  expiresAt: string;
  maxBytes: number;
  blockBytes: number;
};

export type RevisionResponse = LiedRevision & {
  assetId: string;
};

export type AssetAccessResponse = {
  assetId: string;
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  viewUrl: string;
  downloadUrl: string;
  expiresAt: string;
};

export type MaterialSchritt = "uebertragen" | "geprueft";

export class MaterialFehler extends Error {}

export class AbbruchFehler extends MaterialFehler {
  constructor() {
    super("Übertragung wurde abgebrochen.");
  }
}

export type GespeicherteUploadSitzung = {
  uploadSessionId: string;
  fileName: string;
  sizeBytes: number;
  lastModified: number;
  blockBytes: number;
};

const uploadSpeicherPraefix = "arc-upload-";

function uploadSpeicherSchluessel(assetId: string): string {
  return `${uploadSpeicherPraefix}${assetId}`;
}

export function liesUploadSitzung(
  assetId: string,
): GespeicherteUploadSitzung | null {
  try {
    const roh = window.localStorage.getItem(uploadSpeicherSchluessel(assetId));
    if (!roh) return null;
    const eintrag = JSON.parse(roh) as GespeicherteUploadSitzung;
    if (
      typeof eintrag?.uploadSessionId !== "string" ||
      typeof eintrag?.fileName !== "string" ||
      typeof eintrag?.sizeBytes !== "number" ||
      typeof eintrag?.lastModified !== "number" ||
      typeof eintrag?.blockBytes !== "number"
    ) {
      return null;
    }
    return eintrag;
  } catch {
    return null;
  }
}

function speichereUploadSitzung(
  assetId: string,
  sitzung: UploadSessionResponse,
  datei: File,
) {
  try {
    window.localStorage.setItem(
      uploadSpeicherSchluessel(assetId),
      JSON.stringify({
        uploadSessionId: sitzung.uploadSessionId,
        fileName: datei.name,
        sizeBytes: datei.size,
        lastModified: datei.lastModified,
        blockBytes: sitzung.blockBytes,
      }),
    );
  } catch {
    // Ohne lokalen Speicher entfällt nur das Fortsetzen nach dem Neuladen.
  }
}

export function entferneUploadSitzung(assetId: string) {
  try {
    window.localStorage.removeItem(uploadSpeicherSchluessel(assetId));
  } catch {
    // Ohne lokalen Speicher entfällt nur das Fortsetzen nach dem Neuladen.
  }
}

const erlaubteInhaltstypen: Record<AssetType, string[]> = {
  score: ["application/pdf"],
  audio: ["audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/wav", "audio/ogg"],
  midi: ["audio/midi", "audio/x-midi"],
};

const inhaltstypNachEndung: Record<string, string> = {
  pdf: "application/pdf",
  mp3: "audio/mpeg",
  m4a: "audio/mp4",
  mp4: "audio/mp4",
  wav: "audio/wav",
  ogg: "audio/ogg",
  mid: "audio/midi",
  midi: "audio/midi",
};

const stimmKuerzel: Record<string, string> = {
  s: "Sopran",
  a: "Alt",
  t: "Tenor",
  b: "Bass",
};

const stimmNamen: Record<string, string> = {
  sopran: "Sopran",
  alt: "Alt",
  tenor: "Tenor",
  bass: "Bass",
  schlagwerk: "Schlagwerk",
};

function endung(datei: File): string {
  return datei.name.toLowerCase().split(".").pop() ?? "";
}

export async function problemTitel(
  ursache: unknown,
  ersatz: string,
): Promise<string> {
  if (ursache instanceof Response) {
    const inhalt = (await ursache.json().catch(() => null)) as {
      title?: unknown;
    } | null;
    if (inhalt && typeof inhalt.title === "string" && inhalt.title) {
      return inhalt.title;
    }
  }
  return ersatz;
}

export function assetTypFuerDatei(datei: File): AssetType {
  const inhaltstyp = datei.type.toLowerCase();
  if (inhaltstyp === "application/pdf") return "score";
  if (inhaltstyp.startsWith("audio/")) {
    return inhaltstyp.includes("midi") ? "midi" : "audio";
  }
  switch (endung(datei)) {
    case "pdf":
      return "score";
    case "mid":
    case "midi":
      return "midi";
    case "mp3":
    case "m4a":
    case "mp4":
    case "wav":
    case "ogg":
      return "audio";
    default:
      return "score";
  }
}

export function contentTypeFuerAssetTyp(typ: AssetType, datei: File): string {
  const erlaubt = erlaubteInhaltstypen[typ];
  const inhaltstyp = datei.type.toLowerCase();
  if (erlaubt.includes(inhaltstyp)) return inhaltstyp;
  const nachEndung = inhaltstypNachEndung[endung(datei)];
  if (nachEndung && erlaubt.includes(nachEndung)) return nachEndung;
  return erlaubt[0];
}

export function stimmeAusDateiname(
  dateiName: string,
  vorschlaege: string[],
): string {
  const teile = dateiName
    .toLowerCase()
    .split(/[^a-zäöüß]+/)
    .filter(Boolean);
  for (const vorschlag of vorschlaege) {
    if (teile.includes(vorschlag.toLowerCase())) return vorschlag;
  }
  return "";
}

export function stimmenVorschlaege(konfiguration: string | null): string[] {
  const vorschlaege = ["Vollmix"];
  function hinzufuegen(vorschlag: string) {
    if (
      !vorschlag ||
      vorschlaege.some(
        (vorhanden) => vorhanden.toLowerCase() === vorschlag.toLowerCase(),
      )
    ) {
      return;
    }
    vorschlaege.push(vorschlag);
  }
  if (konfiguration) {
    for (const rohteil of konfiguration.split(/[\s,/+]+/)) {
      const teil = rohteil.trim();
      if (!teil) continue;
      const schluessel = teil.toLowerCase();
      const name = stimmNamen[schluessel];
      if (name) {
        hinzufuegen(name);
        continue;
      }
      if (/^[satb]{2,6}$/i.test(schluessel)) {
        for (const kuerzel of schluessel) {
          hinzufuegen(stimmKuerzel[kuerzel]);
        }
        continue;
      }
      if (stimmKuerzel[schluessel]) {
        hinzufuegen(stimmKuerzel[schluessel]);
        continue;
      }
      hinzufuegen(teil);
    }
  }
  return vorschlaege;
}

export async function createAsset(
  versionId: string,
  body: AssetMetadata = {},
): Promise<AssetResponse> {
  const response = await postAuth(
    `/api/musical-versions/${encodeURIComponent(versionId)}/assets`,
    body,
  );
  if (!response.ok) throw response;
  return (await response.json()) as AssetResponse;
}

export async function patchAsset(
  assetId: string,
  body: AssetMetadata,
): Promise<AssetResponse> {
  const response = await patchAuth(
    `/api/assets/${encodeURIComponent(assetId)}`,
    body,
  );
  if (!response.ok) throw response;
  return (await response.json()) as AssetResponse;
}
export type DateiIdentitaet = {
  sizeBytes: number;
  fileName: string;
};

export async function createUploadSession(
  assetId: string,
  identitaet?: DateiIdentitaet,
): Promise<UploadSessionResponse> {
  const response = await postAuth(
    `/api/assets/${encodeURIComponent(assetId)}/upload-session`,
    identitaet ?? {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as UploadSessionResponse;
}

export async function renewUploadSession(
  uploadSessionId: string,
): Promise<UploadSessionResponse> {
  const response = await postAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}/renew`,
    {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as UploadSessionResponse;
}

export async function cancelUploadSession(uploadSessionId: string) {
  await deleteAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}`,
  );
}

export async function finalizeUpload(
  uploadSessionId: string,
  identitaet?: DateiIdentitaet,
): Promise<RevisionResponse> {
  const response = await postAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}/finalize`,
    identitaet ?? {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as RevisionResponse;
}

export async function fetchAssetAccess(
  assetId: string,
  signal?: AbortSignal,
): Promise<AssetAccessResponse> {
  const response = await fetch(
    `/api/assets/${encodeURIComponent(assetId)}/access`,
    {
      credentials: "same-origin",
      cache: "no-store",
      signal,
    },
  );
  if (!response.ok) throw response;
  return (await response.json()) as AssetAccessResponse;
}

/**
 * Verbleibende Laufzeit eines Datei-Tickets in Millisekunden. Ein unparsbares
 * Ablaufdatum zählt als abgelaufen, damit Wiedergaben nicht auf ein
 * möglicherweise veraltetes Ticket bauen.
 */
export function restlaufzeitMs(expiresAt: string, jetzt: number = Date.now()) {
  const ablauf = Date.parse(expiresAt);
  if (Number.isNaN(ablauf)) return 0;
  return ablauf - jetzt;
}

/** Verständliche Meldung eines Material-Spielers. */
export type SpielerFehler = {
  art: "format" | "laden";
  meldung: string;
};

/**
 * Ordnet einer fehlgeschlagenen Ticket- oder Byte-Anfrage eine verständliche
 * Meldung zu; der Ersatztext nennt das Material („Audio …", „MIDI …").
 */
export function ladeFehlerAusUrsache(
  ursache: unknown,
  ersatz: string,
): SpielerFehler {
  const status = ursache instanceof Response ? ursache.status : 0;
  if (status === 404) {
    return {
      art: "laden",
      meldung: "Für dieses Material liegt keine abrufbare Datei vor.",
    };
  }
  if (status === 401) {
    return {
      art: "laden",
      meldung: "Die Anmeldung ist abgelaufen. Bitte lade die Seite neu.",
    };
  }
  return { art: "laden", meldung: ersatz };
}

/** Tabulare Zeitangabe m:ss (beziehungsweise h:mm:ss). */
export function zeitText(sekunden: number) {
  if (!Number.isFinite(sekunden) || sekunden < 0) return "0:00";
  const gesamt = Math.floor(sekunden);
  const stunden = Math.floor(gesamt / 3600);
  const minuten = Math.floor((gesamt % 3600) / 60);
  const rest = gesamt % 60;
  const mm = stunden > 0 ? String(minuten).padStart(2, "0") : String(minuten);
  const ss = String(rest).padStart(2, "0");
  return stunden > 0 ? `${stunden}:${mm}:${ss}` : `${mm}:${ss}`;
}

/** Menschliche Dateigröße in de-AT-Format (B, kB, MB). */
export function groesseText(sizeBytes: number): string {
  const format = new Intl.NumberFormat("de-AT", {
    maximumFractionDigits: 1,
  });
  if (sizeBytes >= 102400) return `${format.format(sizeBytes / 1048576)} MB`;
  if (sizeBytes >= 1024) return `${format.format(sizeBytes / 1024)} kB`;
  return `${format.format(sizeBytes)} B`;
}

async function abschlussFehler(ursache: unknown): Promise<string> {
  if (ursache instanceof Response) {
    const inhalt = (await ursache.json().catch(() => null)) as {
      title?: unknown;
    } | null;
    if (typeof inhalt?.title === "string" && inhalt.title) {
      return inhalt.title;
    }
    if (ursache.status === 409) {
      return "Die Datei wurde noch nicht übertragen. Bitte erneut hochladen.";
    }
    if (ursache.status === 413) return "Die Datei ist zu groß.";
    if (ursache.status === 422) return "Die Datei ist kein gültiges PDF.";
    if (ursache.status === 502) {
      return "Der Speicherdienst ist zurzeit nicht erreichbar. Bitte erneut versuchen.";
    }
  }
  return "Abschluss fehlgeschlagen. Bitte erneut versuchen.";
}

function blockIdFolge(index: number): string {
  return btoa(String(index).padStart(6, "0"));
}

function blockIdIndex(blockId: string): number {
  try {
    return Number(atob(blockId));
  } catch {
    return Number.NaN;
  }
}

function blockPutUrl(uploadUrl: string, index: number): string {
  return `${uploadUrl}&comp=block&blockid=${blockIdFolge(index)}`;
}

function blocklisteUrl(uploadUrl: string): string {
  return `${uploadUrl}&comp=blocklist&blocklisttype=all`;
}

function blocklisteXml(blockIds: string[]): string {
  return `<BlockList>${blockIds
    .map((blockId) => `<Latest>${blockId}</Latest>`)
    .join("")}</BlockList>`;
}

function committierteBloecke(xml: string): string[] {
  if (!xml.trim()) return [];
  const dokument = new DOMParser().parseFromString(xml, "application/xml");
  if (dokument.querySelector("parsererror")) return [];
  const bloecke: string[] = [];
  for (const block of dokument.getElementsByTagName("Name")) {
    const blockId = block.textContent?.trim() ?? "";
    if (blockId && !bloecke.includes(blockId)) bloecke.push(blockId);
  }
  return bloecke.sort((a, b) => blockIdIndex(a) - blockIdIndex(b));
}

function istAbbruch(ursache: unknown): boolean {
  return ursache instanceof DOMException && ursache.name === "AbortError";
}

async function holeBlockliste(
  uploadUrl: string,
  signal?: AbortSignal,
): Promise<string> {
  try {
    const antwort = await fetch(blocklisteUrl(uploadUrl), {
      method: "GET",
      signal,
    });
    if (!antwort.ok) return "";
    return await antwort.text();
  } catch (ursache) {
    if (istAbbruch(ursache)) throw ursache;
    return "";
  }
}

async function putzeBlock(
  uploadUrl: string,
  index: number,
  daten: Blob,
  contentType: string,
  signal?: AbortSignal,
) {
  for (let versuch = 0; versuch < 3; versuch += 1) {
    try {
      const antwort = await fetch(blockPutUrl(uploadUrl, index), {
        method: "PUT",
        headers: { "Content-Type": contentType },
        body: daten,
        signal,
      });
      if (antwort.ok) return;
    } catch (ursache) {
      if (istAbbruch(ursache)) throw ursache;
    }
  }
  throw new MaterialFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
}

async function verbindeBloecke(
  uploadUrl: string,
  blockIds: string[],
  signal?: AbortSignal,
) {
  let antwort: Response;
  try {
    antwort = await fetch(`${uploadUrl}&comp=blocklist`, {
      method: "PUT",
      headers: { "Content-Type": "application/xml" },
      body: blocklisteXml(blockIds),
      signal,
    });
  } catch (ursache) {
    if (istAbbruch(ursache)) throw ursache;
    throw new MaterialFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
  }
  if (!antwort.ok) {
    throw new MaterialFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
  }
}

export async function uebertrageDatei(
  assetId: string,
  datei: File,
  contentType: string,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: {
    onFortschritt?: (uebertragenBytes: number, gesamtBytes: number) => void;
    signal?: AbortSignal;
    sitzung?: UploadSessionResponse;
    committiertAb?: number;
  } = {},
): Promise<RevisionResponse> {
  const identitaet: DateiIdentitaet = {
    sizeBytes: datei.size,
    fileName: datei.name,
  };
  const sitzung =
    optionen.sitzung ?? (await createUploadSession(assetId, identitaet));
  if (optionen.sitzung === undefined) {
    speichereUploadSitzung(assetId, sitzung, datei);
  }
  if (datei.size > sitzung.maxBytes) {
    throw new MaterialFehler("Die Datei ist zu groß.");
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(assetId, sitzung);
  }
  onSchritt?.("uebertragen");
  const blockBytes = Math.max(1, sitzung.blockBytes);
  const gesamt = Math.ceil(datei.size / blockBytes);
  const abIndex = Math.min(optionen.committiertAb ?? 0, gesamt);
  const verbundene = Array.from({ length: abIndex }, (_, i) => blockIdFolge(i));
  let uebertragen = Math.min(abIndex * blockBytes, datei.size);
  optionen.onFortschritt?.(uebertragen, datei.size);
  try {
    for (let index = abIndex; index < gesamt; index += 1) {
      const start = index * blockBytes;
      const daten = datei.slice(
        start,
        Math.min(start + blockBytes, datei.size),
      );
      await putzeBlock(
        sitzung.uploadUrl,
        index,
        daten,
        contentType,
        optionen.signal,
      );
      verbundene.push(blockIdFolge(index));
      uebertragen = Math.min(start + daten.size, datei.size);
      optionen.onFortschritt?.(uebertragen, datei.size);
    }
    await verbindeBloecke(sitzung.uploadUrl, verbundene, optionen.signal);
  } catch (ursache) {
    if (optionen.signal?.aborted || istAbbruch(ursache)) {
      werfeAbbruch(assetId, sitzung);
    }
    throw ursache;
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(assetId, sitzung);
  }
  onSchritt?.("geprueft");
  try {
    const revision = await finalizeUpload(sitzung.uploadSessionId, identitaet);
    entferneUploadSitzung(assetId);
    return revision;
  } catch (ursache) {
    if (optionen.signal?.aborted) {
      werfeAbbruch(assetId, sitzung);
    }
    throw new MaterialFehler(await abschlussFehler(ursache));
  }
}

function werfeAbbruch(assetId: string, sitzung: UploadSessionResponse): never {
  entferneUploadSitzung(assetId);
  void cancelUploadSession(sitzung.uploadSessionId).catch(() => {});
  throw new AbbruchFehler();
}

export async function setzeUploadFort(
  assetId: string,
  datei: File,
  contentType: string,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: {
    onFortschritt?: (uebertragenBytes: number, gesamtBytes: number) => void;
    signal?: AbortSignal;
  } = {},
): Promise<RevisionResponse> {
  const eintrag = liesUploadSitzung(assetId);
  if (!eintrag) {
    throw new MaterialFehler(
      "Keine unterbrochene Übertragung vorhanden. Bitte erneut hochladen.",
    );
  }
  let sitzung: UploadSessionResponse;
  try {
    sitzung = await renewUploadSession(eintrag.uploadSessionId);
  } catch (ursache) {
    throw new MaterialFehler(
      await problemTitel(
        ursache,
        "Die Unterbrechung konnte nicht aufgelöst werden. Bitte erneut versuchen.",
      ),
    );
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(assetId, sitzung);
  }
  const blockBytes = Math.max(1, sitzung.blockBytes);
  const gesamt = Math.ceil(datei.size / blockBytes);
  const committiert = new Set(
    committierteBloecke(
      await holeBlockliste(sitzung.uploadUrl, optionen.signal),
    ),
  );
  let abIndex = 0;
  while (abIndex < gesamt && committiert.has(blockIdFolge(abIndex))) {
    abIndex += 1;
  }
  return await uebertrageDatei(assetId, datei, contentType, onSchritt, {
    ...optionen,
    sitzung,
    committiertAb: abIndex,
  });
}
