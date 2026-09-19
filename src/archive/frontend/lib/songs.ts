export type Lied = {
  id: string;
  title: string;
  composer: string | null;
  lyricist: string | null;
  published: boolean;
  publishedAt: string | null;
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

export type LiedDetails = Lied & {
  createdAt: string;
  updatedAt: string;
  arrangements: LiedArrangement[];
};

export async function fetchSongs(signal?: AbortSignal): Promise<Lied[]> {
  const response = await fetch("/api/songs", {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { songs?: Lied[] };
  return data.songs ?? [];
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
