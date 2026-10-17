import { patchAuth, postAuth } from "@/lib/auth";

// Das flache Lied (#166, ADR-0001): ein Lied mit seinen Dateien. Die alten
// Typen und Abrufe (Song, Fassung, Material) bleiben in lib/songs.ts, solange
// die Auftrittsansichten sie noch brauchen (bis #172).

export type DateiArt = "noten" | "uebe" | "aufnahme" | "sonstiges";

export const dateiArten: [DateiArt, string][] = [
  ["noten", "Noten"],
  ["uebe", "Übe-Dateien"],
  ["aufnahme", "Aufnahmen"],
  ["sonstiges", "Sonstiges"],
];

export function dateiArtText(art: string): string {
  return dateiArten.find(([wert]) => wert === art)?.[1] ?? art;
}

export type LiedDatei = {
  id: string;
  kind: DateiArt;
  isPartitur: boolean;
  stimme: string | null;
  description: string | null;
  fileName: string | null;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
  /** Prüfstand für Löschen und Ersetzen (#168). */
  rowVersion?: number;
  /** Auftritt, bei dem eine Aufnahme entstand (nur wenn sichtbar). */
  recordedAtEvent?: { id: string; title: string; dateDisplay: string } | null;
};

/** Zeile der Liedliste. */
export type LiedEintrag = {
  id: string;
  title: string;
  composer: string | null;
  arranger: string | null;
  lyricist: string | null;
  voiceConfiguration: string | null;
  musicalKey: string | null;
  language: string | null;
  occasion: string | null;
  dateiTypen: DateiArt[];
};

/** Alle Angaben eines Liedes samt seiner (nicht gelöschten) Dateien. */
export type LiedMitDateien = Omit<LiedEintrag, "dateiTypen"> & {
  lyrics: string | null;
  createdAt: string;
  updatedAt: string;
  rowVersion: number;
  dateien: LiedDatei[];
};

export type LiedListe = {
  query: string | null;
  page: number;
  pageSize: number;
  total: number;
  filters: {
    voiceConfiguration: string | null;
    occasion: string | null;
    dateiTypen: DateiArt[];
  };
  options: { voiceConfigurations: string[]; occasions: string[] };
  lieder: LiedEintrag[];
};

export type LiedFilter = {
  besetzung: string;
  anlass: string;
  dateiTypen: DateiArt[];
};

/** Eingabe des Formulars; leere Zeichenketten löschen beim Ändern den Wert. */
export type LiedEingabe = {
  title: string;
  composer: string;
  arranger: string;
  lyricist: string;
  voiceConfiguration: string;
  musicalKey: string;
  language: string;
  lyrics: string;
  occasion: string;
};

export function liedListenPfad(
  query: string | null,
  page: number,
  filter?: LiedFilter,
): string {
  const parameter = new URLSearchParams();
  if (query) parameter.set("q", query);
  if (filter?.besetzung.trim()) {
    parameter.set("besetzung", filter.besetzung.trim());
  }
  if (filter?.anlass.trim()) parameter.set("anlass", filter.anlass.trim());
  for (const art of filter?.dateiTypen ?? []) parameter.append("dateiTyp", art);
  if (page > 1) parameter.set("page", String(page));
  const zeichenkette = parameter.toString();
  return zeichenkette ? `/api/lieder?${zeichenkette}` : "/api/lieder";
}

export async function fetchLiedSuche(
  query: string | null,
  page: number,
  signal?: AbortSignal,
  filter?: LiedFilter,
): Promise<LiedListe> {
  const response = await fetch(liedListenPfad(query, page, filter), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as LiedListe;
}

export async function fetchLied(
  id: string,
  signal?: AbortSignal,
): Promise<LiedMitDateien> {
  const response = await fetch(`/api/lieder/${encodeURIComponent(id)}`, {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return ((await response.json()) as { lied: LiedMitDateien }).lied;
}

export async function legeLiedAn(
  eingabe: LiedEingabe,
): Promise<LiedMitDateien> {
  const response = await postAuth("/api/lieder", eingabe);
  if (!response.ok) throw response;
  return ((await response.json()) as { lied: LiedMitDateien }).lied;
}

export async function aendereLied(
  id: string,
  eingabe: LiedEingabe,
  rowVersion: number,
): Promise<LiedMitDateien> {
  const response = await patchAuth(`/api/lieder/${encodeURIComponent(id)}`, {
    ...eingabe,
    rowVersion,
  });
  if (!response.ok) throw response;
  return ((await response.json()) as { lied: LiedMitDateien }).lied;
}

/** Beschriftung einer Datei in Listen: Partitur, Stimme oder Dateiname. */
export function dateiTitel(datei: LiedDatei): string {
  if (datei.isPartitur) return "Partitur";
  return datei.stimme?.trim() || datei.fileName || "Datei";
}
