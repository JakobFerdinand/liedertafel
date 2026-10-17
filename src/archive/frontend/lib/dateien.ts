import {
  type DateiIdentitaet,
  liesUploadSitzung,
  type MaterialSchritt,
  setzeUploadFort,
  type UploadSessionResponse,
  uebertrageDatei,
} from "@/lib/assets";
import type { Aufnahme } from "@/lib/aufnahmen";
import { patchAuth, postAuth, putAuth } from "@/lib/auth";
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

// Ersetzen, Löschen und Papierkorb (#168).

/** Ersetzt eine Datei durch eine neue; Art, Stimme und Partitur bleiben. */
export async function ersetzeDatei(
  liedId: string,
  alt: LiedDatei,
  datei: File,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: UebertragungOptionen = {},
): Promise<LiedDatei> {
  const contentType =
    alt.kind === "noten" ? "application/pdf" : inhaltstypFuerDatei(datei);
  const antwort = await uebertrageDatei<{ datei: LiedDatei }>(
    `lied-${liedId}-ersetzen-${alt.id}`,
    datei,
    contentType,
    onSchritt,
    {
      ...optionen,
      neueSitzung: async (identitaet) => {
        const response = await postAuth(
          `/api/lieder/${encodeURIComponent(liedId)}/dateien/upload-session`,
          {
            kind: alt.kind,
            replacesDateiId: alt.id,
            contentType,
            ...identitaet,
          },
        );
        if (!response.ok) throw response;
        return (await response.json()) as UploadSessionResponse;
      },
    },
  );
  return antwort.datei;
}

/** Verschiebt eine Datei in den Papierkorb. */
export async function loescheDatei(
  dateiId: string,
  rowVersion: number | undefined,
): Promise<void> {
  const response = await postAuth(
    `/api/dateien/${encodeURIComponent(dateiId)}/delete`,
    { rowVersion },
  );
  if (!response.ok) throw response;
}

export type PapierkorbEintrag = {
  id: string;
  kind: DateiArt;
  isPartitur: boolean;
  stimme: string | null;
  fileName: string | null;
  sizeBytes: number;
  liedId: string | null;
  liedTitle: string | null;
  eventId: string | null;
  eventTitle: string | null;
  deletedAt: string;
  deletedBy: string | null;
  replaced: boolean;
};

export async function fetchPapierkorb(
  signal?: AbortSignal,
): Promise<PapierkorbEintrag[]> {
  const response = await fetch("/api/papierkorb", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return ((await response.json()) as { dateien: PapierkorbEintrag[] }).dateien;
}

// Dateien eines Auftritts (#171): Dokumente, Fotografien und Konzertdateien.

export type NeueAuftrittDatei = {
  kind: "sonstiges" | "aufnahme";
  contentType: string;
  description?: string;
  /** Ersetzt ein Dokument oder Foto des Auftritts (Papierkorb-Regel). */
  replacesDateiId?: string;
};

function auftrittSitzungAnlegen(auftrittId: string, neu: NeueAuftrittDatei) {
  return async (
    identitaet: DateiIdentitaet,
  ): Promise<UploadSessionResponse> => {
    const response = await postAuth(
      `/api/events/${encodeURIComponent(auftrittId)}/dateien/upload-session`,
      {
        kind: neu.kind,
        contentType: neu.contentType,
        description: neu.description?.trim() || null,
        ...(neu.replacesDateiId
          ? { replacesDateiId: neu.replacesDateiId }
          : {}),
        ...identitaet,
      },
    );
    if (!response.ok) throw response;
    return (await response.json()) as UploadSessionResponse;
  };
}

/** Speicherschlüssel der Übertragung (ein Platz je Aufnahme und Platz). */
export function auftrittUploadSchluessel(
  auftrittId: string,
  platz: string,
): string {
  return `auftritt-${auftrittId}-${platz}`;
}

export async function ladeAuftrittDateiHoch(
  auftrittId: string,
  schluessel: string,
  neu: NeueAuftrittDatei,
  datei: File,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: UebertragungOptionen = {},
): Promise<LiedDatei> {
  const antwort = await uebertrageDatei<{ datei: LiedDatei }>(
    schluessel,
    datei,
    neu.contentType,
    onSchritt,
    { ...optionen, neueSitzung: auftrittSitzungAnlegen(auftrittId, neu) },
  );
  return antwort.datei;
}

export async function setzeAuftrittUebertragungFort(
  schluessel: string,
  contentType: string,
  datei: File,
  onSchritt?: (schritt: MaterialSchritt) => void,
  optionen: UebertragungOptionen = {},
): Promise<LiedDatei> {
  const antwort = await setzeUploadFort<{ datei: LiedDatei }>(
    schluessel,
    datei,
    contentType,
    onSchritt,
    optionen,
  );
  return antwort.datei;
}

/** Unterbrochene Übertragungen mit diesem Schlüsselanfang aus dem lokalen Speicher. */
export function unterbrocheneAuftrittUebertragungen(
  auftrittId: string,
  platz: string,
): { schluessel: string; fileName: string }[] {
  const praefix = "arc-upload-";
  const suche = `${praefix}${auftrittUploadSchluessel(auftrittId, platz)}`;
  const eintraege: { schluessel: string; fileName: string }[] = [];
  try {
    for (let i = 0; i < window.localStorage.length; i += 1) {
      const name = window.localStorage.key(i);
      if (!name?.startsWith(suche)) continue;
      const schluessel = name.slice(praefix.length);
      const eintrag = liesUploadSitzung(schluessel);
      if (eintrag) eintraege.push({ schluessel, fileName: eintrag.fileName });
    }
  } catch {
    // Ohne lokalen Speicher entfällt nur das Fortsetzen nach dem Neuladen.
  }
  return eintraege;
}

/** Ändert die Beschreibung eines Dokuments oder Fotos des Auftritts. */
export async function aendereDateiBeschreibung(
  dateiId: string,
  beschreibung: string,
): Promise<void> {
  const response = await patchAuth(
    `/api/dateien/${encodeURIComponent(dateiId)}`,
    { description: beschreibung },
  );
  if (!response.ok) throw response;
}

/**
 * Hängt eine hochgeladene Konzertdatei an den Platz einer Aufnahme; die Datei
 * davor wandert in den Papierkorb. Gibt die ganze Aufnahme zurück.
 */
export async function haengeAufnahmeDateiAn(
  aufnahmeId: string,
  platz: "original" | "playback",
  dateiId: string,
): Promise<Aufnahme> {
  const response = await putAuth(
    `/api/recordings/${encodeURIComponent(aufnahmeId)}/dateien/${platz}`,
    { dateiId },
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { recording: Aufnahme }).recording;
}
