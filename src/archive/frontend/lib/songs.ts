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
  // ARC-023: die Fassungen, die die Fassungsfilter (Stimmverteilung,
  // Begleitung, Tonart, Material) tatsächlich erfüllen — ohne Fassungsfilter
  // alle Fassungen des Liedes.
  matchedArrangements: LiedArrangementKurz[];
  // Neue Liedangaben (ARC-023) im Listenbestand: die Redaktion bearbeitet im
  // Katalog nur Werte, die sie auch sieht.
  language: string | null;
  occasion: string | null;
  tags: string[];
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
  accompaniment: string | null;
  musicalVersions: LiedMusicalVersion[];
};

export type LiedDetails = Omit<
  Lied,
  "arrangements" | "language" | "occasion" | "tags"
> & {
  createdAt: string;
  updatedAt: string;
  lyrics: string | null;
  language: string | null;
  occasion: string | null;
  tags: string[];
  arrangements: LiedArrangement[];
};

// ARC-023: Repertoire-Filter aus der deutschen Katalogadresse. Die
// Materialwerte der Adresse (noten/audio/midi) werden beim Abholen in die
// API-Arten (score/audio/midi) übersetzt; Aufnahmen (ARC-032) ergänzen
// ihren Wert hier.
export type LiedFilter = {
  stimmbesetzung: string;
  begleitung: string;
  tonart: string;
  sprache: string;
  anlass: string;
  tag: string;
  materialien: string[];
};

const materialArten: Record<string, string> = {
  noten: "score",
  audio: "audio",
  midi: "midi",
};

// Adresswerte, die als Materialfilter gelten (ARC-032 ergänzt Aufnahmen
// hier und in der Zuordnung).
export const materialAdressWerte: string[] = Object.keys(materialArten);

export function liedUrlPfad(
  query: string | null,
  page: number,
  filter?: LiedFilter,
): string {
  const parameter = new URLSearchParams();
  if (query) parameter.set("q", query);
  if (filter) {
    // Textfilter werden beschnitten; leer bleibt weg (eine leere
    // Zeichenkette gilt als abwesend).
    const texte: Array<[string, string]> = [
      ["voiceConfiguration", filter.stimmbesetzung],
      ["accompaniment", filter.begleitung],
      ["musicalKey", filter.tonart],
      ["language", filter.sprache],
      ["occasion", filter.anlass],
      ["tag", filter.tag],
    ];
    for (const [name, wert] of texte) {
      const beschnitten = wert.trim();
      if (beschnitten) parameter.set(name, beschnitten);
    }
    const materialien = [
      ...new Set(
        filter.materialien.flatMap((wert) => {
          const art = materialArten[wert];
          return art ? [art] : [];
        }),
      ),
    ];
    if (materialien.length > 0)
      parameter.set("material", materialien.join(","));
  }
  if (page > 1) parameter.set("page", String(page));
  const zeichenkette = parameter.toString();
  return zeichenkette ? `/api/songs?${zeichenkette}` : "/api/songs";
}

export async function fetchSongSearch(
  query: string | null,
  page: number,
  signal?: AbortSignal,
  filter?: LiedFilter,
): Promise<LiedSuchErgebnis> {
  const response = await fetch(liedUrlPfad(query, page, filter), {
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
