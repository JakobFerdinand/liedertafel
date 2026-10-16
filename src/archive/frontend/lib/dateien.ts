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
};

/**
 * Speicherschlüssel der unterbrochenen Übertragung: ein Platz je Lied und
 * Partitur/Stimme, damit ein Neuladen die Übertragung fortsetzen kann.
 */
export function uploadSchluessel(liedId: string, neu: NeueDatei): string {
  const platz = neu.isPartitur ? "partitur" : neu.stimme.trim().toLowerCase();
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
        stimme: neu.isPartitur ? null : neu.stimme.trim(),
        description: neu.description?.trim() || null,
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
    "application/pdf",
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
    "application/pdf",
    onSchritt,
    optionen,
  );
  return antwort.datei;
}
