import { patchAuth, postAuth } from "@/lib/auth";
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
  uploadUrl: string;
  expiresAt: string;
  maxBytes: number;
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

export async function createUploadSession(
  assetId: string,
): Promise<UploadSessionResponse> {
  const response = await postAuth(
    `/api/assets/${encodeURIComponent(assetId)}/upload-session`,
    {},
  );
  if (!response.ok) throw response;
  return (await response.json()) as UploadSessionResponse;
}

export async function finalizeUpload(
  uploadSessionId: string,
): Promise<RevisionResponse> {
  const response = await postAuth(
    `/api/upload-sessions/${encodeURIComponent(uploadSessionId)}/finalize`,
    {},
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

function abschlussFehler(ursache: unknown): string {
  if (ursache instanceof Response) {
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

export async function uebertrageDatei(
  assetId: string,
  datei: File,
  contentType: string,
  onSchritt?: (schritt: MaterialSchritt) => void,
): Promise<RevisionResponse> {
  const sitzung = await createUploadSession(assetId);
  if (datei.size > sitzung.maxBytes) {
    throw new MaterialFehler("Die Datei ist zu groß.");
  }
  onSchritt?.("uebertragen");
  let uebertragung: Response | null = null;
  try {
    uebertragung = await fetch(sitzung.uploadUrl, {
      method: "PUT",
      headers: { "Content-Type": contentType },
      body: datei,
    });
  } catch {
    throw new MaterialFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
  }
  if (!uebertragung.ok) {
    throw new MaterialFehler("Übertragung zum Speicherdienst fehlgeschlagen.");
  }
  onSchritt?.("geprueft");
  try {
    return await finalizeUpload(sitzung.uploadSessionId);
  } catch (ursache) {
    throw new MaterialFehler(
      await problemTitel(ursache, abschlussFehler(ursache)),
    );
  }
}
