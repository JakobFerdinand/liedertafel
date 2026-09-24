// ARC-024: Auftritte des Chores mit ehrlicher Datumsdarstellung. Die
// Abrufhelfer folgen dem Muster der Lieder (deutsche Fehler als geworfene
// Antworten, keine Zwischenspeicherung).

// Geschlossene Artenmenge des Vertrags; die deutschen Namen liest die
// Oberfläche daraus.
export type AuftrittArt =
  | "concert"
  | "service"
  | "wedding"
  | "funeral"
  | "festival"
  | "other";

export type Auftritt = {
  id: string;
  kind: string;
  title: string;
  venue: string | null;
  dateYear: number | null;
  dateMonth: number | null;
  dateDay: number | null;
  dateApproximate: boolean;
  // day = genaues Datum, month = Monat bekannt, year = nur das Jahr,
  // unknown = nichts überliefert (kein erfundenes Kalenderdatum).
  datePrecision: "day" | "month" | "year" | "unknown";
  // Serverseitig geformte deutsche Anzeige: „12. Mai 1950“, „um 1950“
  // oder „Datum unbekannt“.
  dateDisplay: string;
  startTime: string | null;
  published: boolean;
};

export type AuftrittDetails = Auftritt & {
  notes: string | null;
  sourceNote: string | null;
  createdAt: string;
  updatedAt: string;
  publishedAt: string | null;
};

// Die Jahreszusammenfassung der ganzen sichtbaren Menge für die
// Jahresleiste; Auftritte ohne Datum laufen unter year: null.
export type AuftrittJahr = { year: number | null; count: number };

export type AuftrittSuchErgebnis = {
  years: AuftrittJahr[];
  total: number;
  events: Auftritt[];
};

export const auftrittArten: Record<string, string> = {
  concert: "Konzert",
  service: "Gottesdienst",
  wedding: "Hochzeit",
  funeral: "Bestattung",
  festival: "Fest",
  other: "Sonstiger Auftritt",
};

export function auftrittArtName(kind: string): string {
  return auftrittArten[kind] ?? "Sonstiger Auftritt";
}

export function auftrittUrlPfad(auswahl: {
  year?: number | null;
  kind?: string | null;
}): string {
  const parameter = new URLSearchParams();
  if (typeof auswahl.year === "number") {
    parameter.set("year", String(auswahl.year));
  }
  const kind = auswahl.kind?.trim();
  if (kind) parameter.set("kind", kind);
  const zeichenkette = parameter.toString();
  return zeichenkette ? `/api/events?${zeichenkette}` : "/api/events";
}

export async function fetchEvents(
  auswahl: { year?: number | null; kind?: string | null } = {},
  signal?: AbortSignal,
): Promise<AuftrittSuchErgebnis> {
  const response = await fetch(auftrittUrlPfad(auswahl), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as AuftrittSuchErgebnis;
}

export async function fetchEvent(
  id: string,
  signal?: AbortSignal,
): Promise<AuftrittDetails> {
  const response = await fetch(`/api/events/${encodeURIComponent(id)}`, {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  const data = (await response.json()) as { event: AuftrittDetails };
  return data.event;
}
