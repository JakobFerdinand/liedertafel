export type Lied = {
  id: string;
  title: string;
  composer: string | null;
  lyricist: string | null;
  published: boolean;
  publishedAt: string | null;
  alternateTitles: string[];
  arrangements: LiedArrangementKurz[];
  matchedIn: string[];
};

export type LiedArrangementKurz = {
  id: string;
  label: string;
  arranger: string | null;
};

export type LiedSuchErgebnis = {
  query: string | null;
  page: number;
  pageSize: number;
  total: number;
  songs: Lied[];
};

export type LiedRevision = {
  revisionId: string;
  revisionNumber: number;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
};

export type LiedAsset = {
  id: string;
  assetType: string;
  voiceLabel: string | null;
  description: string | null;
  currentRevision: LiedRevision | null;
};

export type LiedMusicalVersion = {
  id: string;
  label: string;
  creator: string | null;
  musicalKey: string | null;
  assets: LiedAsset[];
};

export type LiedArrangement = {
  id: string;
  label: string;
  arranger: string | null;
  voiceConfiguration: string | null;
  musicalVersions: LiedMusicalVersion[];
};

export type LiedDetails = Omit<Lied, "arrangements"> & {
  createdAt: string;
  updatedAt: string;
  lyrics: string | null;
  arrangements: LiedArrangement[];
};

export function liedUrlPfad(query: string | null, page: number): string {
  const parameter = new URLSearchParams();
  if (query) parameter.set("q", query);
  if (page > 1) parameter.set("page", String(page));
  const zeichenkette = parameter.toString();
  return zeichenkette ? `/api/songs?${zeichenkette}` : "/api/songs";
}

export async function fetchSongSearch(
  query: string | null,
  page: number,
  signal?: AbortSignal,
): Promise<LiedSuchErgebnis> {
  const response = await fetch(liedUrlPfad(query, page), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as LiedSuchErgebnis;
}

export async function fetchSong(
  id: string,
  signal?: AbortSignal,
): Promise<LiedDetails> {
  const response = await fetch(`/api/songs/${encodeURIComponent(id)}`, {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { song: LiedDetails };
  return data.song;
}
