import { groesseText } from "@/lib/assets";
import { patchAuth, postAuth } from "@/lib/auth";

// ARC-030: ganze Aufnahmen eines Auftritts. Die Typen spiegeln die Antworten
// von /api/events/{id}/recordings und /api/recordings/{id}/… Feld für Feld.

export type AufnahmeArt = "audio" | "video";

/** ready: abspielbar · needsPlaybackCopy: Datei da, aber nicht abspielbar · missing: keine Datei. */
export type AbspielZustand = "ready" | "needsPlaybackCopy" | "missing";

export type AufnahmeDatei = {
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  fileName: string | null;
  createdAt: string;
  /** Vom Server als im Browser abspielbarer Container erkannt. */
  playable: boolean;
};

export type AufnahmePlatz = {
  assetId: string;
  file: AufnahmeDatei | null;
};

export type Aufnahme = {
  id: string;
  eventId: string;
  label: string;
  kind: AufnahmeArt;
  isPublished: boolean;
  downloadEnabled: boolean;
  durationSeconds: number | null;
  playback: {
    state: AbspielZustand;
    source: "original" | "playbackCopy" | null;
    revisionId: string | null;
    contentType: string | null;
    sizeBytes: number | null;
  };
  createdAt: string;
  /** Nur für die Redaktion; Mitglieder erhalten null. */
  editor: {
    version: number;
    /** Dateien ändert nur, wer die Aufnahme angelegt hat. */
    canChangeFiles: boolean;
    publishedAt: string | null;
    original: AufnahmePlatz;
    playbackCopy: AufnahmePlatz | null;
  } | null;
};

export type AufnahmeZugriff = {
  recordingId: string;
  kind: AufnahmeArt;
  playbackState: AbspielZustand;
  source: "original" | "playbackCopy" | null;
  revisionId: string;
  contentType: string;
  sizeBytes: number;
  durationSeconds: number | null;
  /** null, solange keine abspielbare Datei vorliegt. */
  viewUrl: string | null;
  downloadEnabled: boolean;
  /** null, solange die Redaktion das Herunterladen nicht freigegeben hat. */
  downloadUrl: string | null;
  expiresAt: string;
};

export type AufnahmeAenderung = {
  label?: string;
  isPublished?: boolean;
  downloadEnabled?: boolean;
  durationSeconds?: number;
  /** Stand, aus dem das Formular gebaut wurde; schützt vor Überschreiben. */
  expectedVersion?: number;
};

/** Vertrag RecordingEndpoints.ConcurrencyMessage: veralteter Stand, neu laden. */
export const AufnahmeVeraltet = "Die Aufnahme wurde zwischenzeitlich geändert.";

export async function fetchAufnahmen(
  eventId: string,
  signal?: AbortSignal,
): Promise<Aufnahme[]> {
  const response = await fetch(
    `/api/events/${encodeURIComponent(eventId)}/recordings`,
    { credentials: "same-origin", cache: "no-store", signal },
  );
  if (!response.ok) throw response;
  const daten = (await response.json()) as { recordings: Aufnahme[] };
  return daten.recordings;
}

/**
 * Legt die Aufnahme samt leerem Original-Platz an. Am Auftritt selbst ändert
 * sich nichts: kein Programm, keine Aufführung.
 */
export async function createAufnahme(
  eventId: string,
  body: { label: string; kind: AufnahmeArt },
): Promise<Aufnahme> {
  const response = await postAuth(
    `/api/events/${encodeURIComponent(eventId)}/recordings`,
    body,
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { recording: Aufnahme }).recording;
}

export async function patchAufnahme(
  id: string,
  body: AufnahmeAenderung,
): Promise<Aufnahme> {
  const response = await patchAuth(
    `/api/recordings/${encodeURIComponent(id)}`,
    body,
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { recording: Aufnahme }).recording;
}

/** Öffnet den Platz für eine umgewandelte Abspielfassung (wiederholbar). */
export async function oeffneAbspielfassung(id: string): Promise<Aufnahme> {
  const response = await postAuth(
    `/api/recordings/${encodeURIComponent(id)}/playback`,
    {},
  );
  if (!response.ok) throw response;
  return ((await response.json()) as { recording: Aufnahme }).recording;
}

/** Ticket der Aufnahme; derselbe Aufruf erneuert es. */
export async function fetchAufnahmeZugriff(
  id: string,
  signal?: AbortSignal,
): Promise<AufnahmeZugriff> {
  const response = await fetch(
    `/api/recordings/${encodeURIComponent(id)}/access`,
    { credentials: "same-origin", cache: "no-store", signal },
  );
  if (!response.ok) throw response;
  return (await response.json()) as AufnahmeZugriff;
}

export function aufnahmeArtName(art: AufnahmeArt): string {
  return art === "video" ? "Video" : "Tonaufnahme";
}

/** Lesbarer Name des Dateiformats; Unbekanntes bleibt ehrlich unbekannt. */
export function formatName(contentType: string): string {
  switch (contentType.toLowerCase()) {
    case "video/mp4":
      return "MP4";
    case "audio/mp4":
      return "M4A";
    case "video/webm":
    case "audio/webm":
      return "WebM";
    case "audio/mpeg":
      return "MP3";
    case "audio/wav":
      return "WAV";
    case "video/quicktime":
      return "QuickTime (MOV)";
    case "video/x-matroska":
      return "Matroska (MKV)";
    case "video/x-msvideo":
      return "AVI";
    case "video/3gpp":
      return "3GP";
    case "audio/flac":
      return "FLAC";
    case "audio/ogg":
    case "video/ogg":
      return "Ogg";
    default:
      return "unbekanntes Format";
  }
}

/** Dateigröße in de-AT-Format; ganze Aufnahmen erreichen Gigabyte. */
export function aufnahmeGroesse(sizeBytes: number): string {
  if (sizeBytes < 1073741824) return groesseText(sizeBytes);
  const format = new Intl.NumberFormat("de-AT", { maximumFractionDigits: 1 });
  return `${format.format(sizeBytes / 1073741824)} GB`;
}

/** Art-Vorschlag aus der gewählten Datei; ohne Hinweis bleibt es Video. */
export function artFuerDatei(datei: File): AufnahmeArt {
  if (datei.type.startsWith("audio/")) return "audio";
  if (/\.(mp3|m4a|wav|flac|ogg|aac)$/i.test(datei.name)) return "audio";
  return "video";
}

/**
 * Misst die Länge einer lokalen Datei im Browser, ohne sie abzuspielen.
 * null, wenn dieser Browser die Datei nicht lesen kann oder nicht rechtzeitig
 * antwortet — die Länge bleibt dann ehrlich unbekannt.
 */
export function messeDauer(
  datei: File,
  art: AufnahmeArt,
): Promise<number | null> {
  return new Promise((fertig) => {
    const adresse = URL.createObjectURL(datei);
    const medium = document.createElement(art === "video" ? "video" : "audio");
    let erledigt = false;
    const ende = (wert: number | null) => {
      if (erledigt) return;
      erledigt = true;
      window.clearTimeout(frist);
      medium.removeAttribute("src");
      medium.load();
      URL.revokeObjectURL(adresse);
      fertig(wert);
    };
    const frist = window.setTimeout(() => ende(null), 15_000);
    medium.preload = "metadata";
    medium.onloadedmetadata = () => {
      const dauer = medium.duration;
      ende(Number.isFinite(dauer) && dauer > 0 ? dauer : null);
    };
    medium.onerror = () => ende(null);
    medium.src = adresse;
  });
}
