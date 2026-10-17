import {
  type DateiIdentitaet,
  liesUploadSitzung,
  type MaterialSchritt,
  setzeUploadFort,
  type UploadSessionResponse,
  uebertrageDatei,
} from "@/lib/assets";
import { postAuth } from "@/lib/auth";
import type { DateiArt, LiedDatei } from "@/lib/lieder";

// Dateien eines Liedes (#166): Zugriff und Hochladen über die gemeinsame
// Übertragung aus lib/assets.ts. Spätere Tickets ergänzen hier nur Exporte.

export type DateiZugriff = {
  dateiId: string;
  kind: DateiArt;
  contentType: string;
  sizeBytes: number;
  fileName: string | null;
  createdAt: string;
  viewUrl: string;
  downloadUrl: string;
  expiresAt: string;
};

export async function fetchDateiZugriff(
  dateiId: string,
  signal?: AbortSignal,
): Promise<DateiZugriff> {
  const response = await fetch(
    `/api/dateien/${encodeURIComponent(dateiId)}/access`,
    { credentials: "same-origin", cache: "no-store", signal },
  );
  if (!response.ok) throw response;
  return (await response.json()) as DateiZugriff;
}

export type NeueDatei = {
  kind: DateiArt;
  isPartitur: boolean;
  stimme: string;
  description?: string;
  contentType?: string;
  /** Dateiname; Schlüssel der Übertragung, wo es keine Stimme gibt. */
  dateiName?: string;
  recordedAtEventId?: string | null;
};

/**
 * Speicherschlüssel der unterbrochenen Übertragung: ein Platz je Lied und
 * Partitur/Stimme, damit ein Neuladen die Übertragung fortsetzen kann.
 */
export function uploadSchluessel(liedId: string, neu: NeueDatei): string {
  const platz = neu.isPartitur
    ? "partitur"
    : neu.kind === "aufnahme" || neu.kind === "sonstiges"
      ? (neu.dateiName ?? "").toLowerCase()
      : neu.stimme.trim().toLowerCase();
  return `lied-${liedId}-${neu.kind}-${platz}`;
}

export function gespeicherteUebertragung(liedId: string, neu: NeueDatei) {
  return liesUploadSitzung(uploadSchluessel(liedId, neu));
}

function sitzungAnlegen(liedId: string, neu: NeueDatei) {
  return async (
    identitaet: DateiIdentitaet,
  ): Promise<UploadSessionResponse> => {
    const response = await postAuth(
      `/api/lieder/${encodeURIComponent(liedId)}/dateien/upload-session`,
      {
        kind: neu.kind,
        isPartitur: neu.isPartitur,
        stimme: neu.isPartitur ? null : neu.stimme.trim() || null,
        description: neu.description?.trim() || null,
        contentType: neu.contentType ?? "application/pdf",
        ...(neu.recordedAtEventId
          ? { recordedAtEventId: neu.recordedAtEventId }
          : {}),
        ...identitaet,
      },
    );
    if (!response.ok) throw response;
    return (await response.json()) as UploadSessionResponse;
  };
}

type UebertragungOptionen = {
  onFortschritt?: (uebertragenBytes: number, gesamtBytes: number) => void;
  signal?: AbortSignal;
};

export async function ladeDateiHoch(
  liedId: string,
  neu: NeueDatei,
  datei: File,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: UebertragungOptionen = {},
): Promise<LiedDatei> {
  const antwort = await uebertrageDatei<{ datei: LiedDatei }>(
    uploadSchluessel(liedId, neu),
    datei,
    neu.contentType ?? "application/pdf",
    onSchritt,
    { ...optionen, neueSitzung: sitzungAnlegen(liedId, neu) },
  );
  return antwort.datei;
}

export async function setzeDateiUebertragungFort(
  liedId: string,
  neu: NeueDatei,
  datei: File,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: UebertragungOptionen = {},
): Promise<LiedDatei> {
  const antwort = await setzeUploadFort<{ datei: LiedDatei }>(
    uploadSchluessel(liedId, neu),
    datei,
    neu.contentType ?? "application/pdf",
    onSchritt,
    optionen,
  );
  return antwort.datei;
}

const inhaltstypNachEndung: Record<string, string> = {
  pdf: "application/pdf",
  mp3: "audio/mpeg",
  m4a: "audio/mp4",
  wav: "audio/wav",
  ogg: "audio/ogg",
  weba: "audio/webm",
  mid: "audio/midi",
  midi: "audio/midi",
  mp4: "video/mp4",
  webm: "video/webm",
  jpg: "image/jpeg",
  jpeg: "image/jpeg",
  png: "image/png",
  webp: "image/webp",
  txt: "text/plain",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
};

/** Inhaltstyp einer gewählten Datei: erst der Browser, sonst die Endung. */
export function inhaltstypFuerDatei(datei: File): string {
  const endung = datei.name.toLowerCase().split(".").pop() ?? "";
  const nachEndung = inhaltstypNachEndung[endung];
  if (nachEndung) return nachEndung;
  return datei.type.toLowerCase() || "application/octet-stream";
}
