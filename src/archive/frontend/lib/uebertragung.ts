import { deleteAuth, postAuth } from "@/lib/auth";

// Gemeinsame Übertragung (blockweises Hochladen in den Speicherdienst mit
// Fortsetzen nach Unterbrechung) für alle Dateien von Liedern und Auftritten,
// dazu die kleinen Hilfen der Player.

export type UploadSessionResponse = {
  uploadSessionId: string;
  blobName: string | null;
  uploadUrl: string;
  expiresAt: string;
  maxBytes: number;
  blockBytes: number;
};

export type MaterialSchritt = "uebertragen" | "geprueft";

export class MaterialFehler extends Error {
  /** HTTP-Status der abgewiesenen Anfrage, falls der Server geantwortet hat. */
  readonly status: number | null;

  constructor(meldung: string, status: number | null = null) {
    super(meldung);
    this.status = status;
  }
}

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

function uploadSpeicherSchluessel(schluessel: string): string {
  return `${uploadSpeicherPraefix}${schluessel}`;
}

export function liesUploadSitzung(
  schluessel: string,
): GespeicherteUploadSitzung | null {
  try {
    const roh = window.localStorage.getItem(
      uploadSpeicherSchluessel(schluessel),
    );
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
  schluessel: string,
  sitzung: UploadSessionResponse,
  datei: File,
) {
  try {
    window.localStorage.setItem(
      uploadSpeicherSchluessel(schluessel),
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

export function entferneUploadSitzung(schluessel: string) {
  try {
    window.localStorage.removeItem(uploadSpeicherSchluessel(schluessel));
  } catch {
    // Ohne lokalen Speicher entfällt nur das Fortsetzen nach dem Neuladen.
  }
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

export type DateiIdentitaet = {
  sizeBytes: number;
  fileName: string;
};

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

export async function finalizeUpload<T>(
  uploadSessionId: string,
  identitaet?: DateiIdentitaet,
): Promise<T> {
  const response = await postAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}/finalize`,
    identitaet ?? {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as T;
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

/**
 * Überträgt eine Datei blockweise und schließt die Sitzung ab. `schluessel` ist
 * der Speicherschlüssel der Sitzung (arc-upload-<Schlüssel>); `optionen.neueSitzung`
 * legt die Sitzung an und der Rückgabetyp ist die Antwort des Abschlusses.
 */
export async function uebertrageDatei<T>(
  schluessel: string,
  datei: File,
  contentType: string,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: {
    onFortschritt?: (uebertragenBytes: number, gesamtBytes: number) => void;
    signal?: AbortSignal;
    sitzung?: UploadSessionResponse;
    committiertAb?: number;
    neueSitzung?: (
      identitaet: DateiIdentitaet,
    ) => Promise<UploadSessionResponse>;
  } = {},
): Promise<T> {
  const identitaet: DateiIdentitaet = {
    sizeBytes: datei.size,
    fileName: datei.name,
  };
  let sitzung = optionen.sitzung;
  if (sitzung === undefined) {
    if (optionen.neueSitzung === undefined) {
      throw new MaterialFehler(
        "Es gibt keine Übertragung, die begonnen werden kann.",
      );
    }
    sitzung = await optionen.neueSitzung(identitaet);
    speichereUploadSitzung(schluessel, sitzung, datei);
  }
  if (datei.size > sitzung.maxBytes) {
    throw new MaterialFehler("Die Datei ist zu groß.");
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(schluessel, sitzung);
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
      werfeAbbruch(schluessel, sitzung);
    }
    throw ursache;
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(schluessel, sitzung);
  }
  onSchritt?.("geprueft");
  try {
    const antwort = await schliesseUploadAb<T>(
      sitzung.uploadSessionId,
      identitaet,
    );
    entferneUploadSitzung(schluessel);
    return antwort;
  } catch (ursache) {
    if (optionen.signal?.aborted) {
      werfeAbbruch(schluessel, sitzung);
    }
    throw new MaterialFehler(
      await abschlussFehler(ursache),
      ursache instanceof Response ? ursache.status : null,
    );
  }
}

/** Vertrag UploadEndpoints.ConcurrencyMessage: gleichzeitige Änderung verloren. */
const gleichzeitigGeaendert = "Der Eintrag wurde zwischenzeitlich geändert.";

/**
 * Schließt den Upload ab. Hat eine gleichzeitige Korrektur gewonnen (409 mit
 * der Meldung oben), bleibt die Sitzung samt übertragener Datei bestehen —
 * ein zweiter Abschluss legt dann den nächsten Dateistand an, ohne die Datei
 * erneut zu übertragen. Andere 409 (Datei fehlt, abgelaufen, abgebrochen)
 * sind endgültig und werden nicht wiederholt.
 */
async function schliesseUploadAb<T>(
  uploadSessionId: string,
  identitaet: DateiIdentitaet,
): Promise<T> {
  try {
    return await finalizeUpload<T>(uploadSessionId, identitaet);
  } catch (ursache) {
    if (!(ursache instanceof Response) || ursache.status !== 409) throw ursache;
    const titel = await problemTitel(ursache.clone(), "");
    if (titel !== gleichzeitigGeaendert) throw ursache;
    return await finalizeUpload<T>(uploadSessionId, identitaet);
  }
}

function werfeAbbruch(
  schluessel: string,
  sitzung: UploadSessionResponse,
): never {
  entferneUploadSitzung(schluessel);
  void cancelUploadSession(sitzung.uploadSessionId).catch(() => {});
  throw new AbbruchFehler();
}

export async function setzeUploadFort<T>(
  schluessel: string,
  datei: File,
  contentType: string,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: {
    onFortschritt?: (uebertragenBytes: number, gesamtBytes: number) => void;
    signal?: AbortSignal;
  } = {},
): Promise<T> {
  const eintrag = liesUploadSitzung(schluessel);
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
      ursache instanceof Response ? ursache.status : null,
    );
  }
  if (optionen.signal?.aborted) {
    werfeAbbruch(schluessel, sitzung);
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
  return await uebertrageDatei<T>(schluessel, datei, contentType, onSchritt, {
    ...optionen,
    sitzung,
    committiertAb: abIndex,
  });
}
