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

/** Aus dem Text der aktuellen Noten gelesene Angaben (Vertrag ScoreFacts). */
export type NotenAngaben = {
  voice: string | null;
  musicalKey: string | null;
  timeSignature: string | null;
  tempo: string | null;
  composer: string | null;
  lyricist: string | null;
  arranger: string | null;
  copyright: string | null;
};

export type LiedAsset = {
  id: string;
  assetType: string;
  voiceLabel: string | null;
  description: string | null;
  /** Fehlt in den Antworten der Material-Endpunkte; nur das Lieddetail trägt es. */
  detected?: NotenAngaben | null;
  currentRevision: LiedRevision | null;
};

export type LiedArrangement = {
  id: string;
  label: string;
  arranger: string | null;
  voiceConfiguration: string | null;
  accompaniment: string | null;
  /** ARC-013-1: Optimistischer Zeiger der Arrangement-Zeile. */
  rowVersion?: number;
  musicalVersions: LiedMusicalVersion[];
};

export type LiedMusicalVersion = {
  id: string;
  label: string;
  creator: string | null;
  musicalKey: string | null;
  /** ARC-013-1: Optimistischer Zeiger der Fassungs-Zeile. */
  rowVersion?: number;
  assets: LiedAsset[];
};

/** ARC-013-1: neueste Herkunft eines Einzelfeldes (nur Redaktion). */
export type FeldHerkunft = {
  entityType: "song" | "arrangement" | "musical_version" | "asset" | "event";
  entityId: string;
  field: string;
  fieldLabel: string;
  source: "Human" | "Regex" | "Ai";
  confidence: "Sicher" | "Unsicher";
  model: string | null;
  promptVersion: string | null;
  changedAt: string;
  locked: boolean;
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
  /** ARC-013-1: Optimistischer Zeiger der Lied-Zeile. */
  rowVersion?: number;
  /** ARC-013-1: Feldherkunft der Redaktion; Mitglieder erhalten kein Feld. */
  provenance?: FeldHerkunft[];
  arrangements: LiedArrangement[];
};

// ARC-023: Repertoire-Filter aus der deutschen Katalogadresse. Die
// Materialwerte der Adresse (noten/audio/midi/aufnahme) werden beim Abholen
// in die API-Arten (score/audio/midi/recording) übersetzt; „aufnahme“ meint
// Lieder mit einer markierten Stelle in einer Aufnahme (ARC-032).
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
  aufnahme: "recording",
};

// Adresswerte, die als Materialfilter gelten.
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

/**
 * Anzeigename eines Materials: die eingetragene Stimme gewinnt, sonst die
 * aus den Noten erkannte; ohne beides heißen Noten „Noten“ und Aufnahmen
 * „Vollmix“.
 */
export function materialStimme(asset: LiedAsset): string {
  return (
    asset.voiceLabel?.trim() ||
    asset.detected?.voice ||
    (asset.assetType === "score" ? "Noten" : "Vollmix")
  );
}

/**
 * Tonart einer Fassung: die eingetragene, sonst die aus den Noten erkannte —
 * diese nur, wenn alle Noten der Fassung dieselbe nennen.
 */
export function fassungTonart(fassung: LiedMusicalVersion): string | null {
  if (fassung.musicalKey) return fassung.musicalKey;
  const erkannt = new Set(
    // Antworten der Fassungs-Endpunkte tragen keine Materialliste.
    (fassung.assets ?? [])
      .map((asset) => asset.detected?.musicalKey)
      .filter((tonart): tonart is string => Boolean(tonart)),
  );
  return erkannt.size === 1 ? [...erkannt][0] : null;
}

/** Erkannte Angaben als lesbare Teile, etwa „Tonart A♭“ und „4/4-Takt“. */
export function notenAngabenTeile(
  angaben: NotenAngaben | null | undefined,
): string[] {
  if (!angaben) return [];
  return [
    angaben.musicalKey ? `Tonart ${angaben.musicalKey}` : null,
    angaben.timeSignature ? `${angaben.timeSignature}-Takt` : null,
    angaben.tempo ? `Tempo ${angaben.tempo}` : null,
    angaben.composer ? `Musik: ${angaben.composer}` : null,
    angaben.lyricist ? `Text: ${angaben.lyricist}` : null,
    angaben.arranger ? `Satz: ${angaben.arranger}` : null,
    angaben.copyright,
  ].filter((teil): teil is string => Boolean(teil));
}
