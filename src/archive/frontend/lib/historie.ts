// ARC-031: Aufführungsgeschichte eines Liedes. Die Zeilen sind die
// Aufführungsnachweise (ARC-028) mit ihrer stabilen Id — daran hängt die
// Aufnahmenverknüpfung (ARC-032). Die Zahlen sind ehrlich begrenzt: sie
// beschreiben nur die erfasste Überlieferung, bestätigte Aufführungen und
// unbestätigte Programmangaben bleiben getrennt, mögliche Doppelerfassungen
// werden gekennzeichnet statt zusammengeführt.

export type HistorieSong = {
  id: string;
  title: string;
  published: boolean;
};

export type HistorieZaehlung = {
  confirmed: {
    occurrences: number;
    events: number;
    uncertainDates: number;
    // Handerfasste bestätigte Zeilen neben einer über das Programm
    // bestätigten Zeile am selben Auftritt: die Zahl liest sich als Obergrenze.
    possiblyDuplicate: number;
  };
  unconfirmed: {
    occurrences: number;
    events: number;
    // Auftritte, an denen für das Lied nur eine Programmangabe vorliegt.
    onlyEvents: number;
    uncertainDates: number;
  };
  // Nur für die Redaktion: Nachweise an unveröffentlichten Auftritten, die
  // in keiner Zahl stehen. Mitglieder erhalten null.
  draftEventOccurrences: number | null;
};

export type HistorieFassungZahl = {
  id: string;
  label: string;
  confirmed: number;
  unconfirmed: number;
};

export type HistorieZeile = {
  id: string;
  eventId: string;
  eventTitle: string;
  eventKind: string;
  eventPublished: boolean;
  dateYear: number | null;
  dateMonth: number | null;
  dateDay: number | null;
  dateApproximate: boolean;
  dateDisplay: string;
  datePrecision: "day" | "month" | "year" | "unknown";
  dateUncertain: boolean;
  evidenceStatus: string;
  // programme = über die Programmbestätigung entstanden (ARC-029), auch nach
  // einer Herabstufung zur Programmangabe; record = aus einer Archivquelle
  // erfasst (ARC-028). Nur zusammen mit evidenceStatus lesen.
  origin: "programme" | "record";
  arrangement: { id: string; label: string } | null;
  musicalVersion: { id: string; label: string } | null;
  // Stelle unter den bestätigten Aufführungen des Liedes an diesem Auftritt.
  occurrence: { index: number; of: number } | null;
  possiblyDuplicate: boolean;
  // Auf jeder bestätigten Zeile eines Auftritts, an dem eine bestätigte
  // Zeile möglicherweise doppelt erfasst ist: die Anzahl dort ist eine
  // Obergrenze, keine gesicherte Wiederholung.
  possiblyDuplicateAtEvent: boolean;
  alsoConfirmedAtEvent: boolean;
  // Nur für die Redaktion.
  sourceNote?: string | null;
};

export type LiedHistorie = {
  song: HistorieSong;
  page: number;
  pageSize: number;
  total: number;
  counts: HistorieZaehlung;
  arrangements: HistorieFassungZahl[];
  unknownArrangement: { confirmed: number; unconfirmed: number };
  performances: HistorieZeile[];
};

export type HistorieAuswahl = {
  page?: number;
  // "confirmed" | "mention"; leer = alle Nachweise.
  evidence?: string;
  // Arrangement-Id oder "unknown"; leer = alle Fassungen.
  arrangementId?: string;
};

export function historieUrlPfad(
  songId: string,
  auswahl: HistorieAuswahl = {},
): string {
  const parameter = new URLSearchParams();
  if (auswahl.page && auswahl.page > 1) {
    parameter.set("page", String(auswahl.page));
  }
  if (auswahl.evidence) parameter.set("evidence", auswahl.evidence);
  if (auswahl.arrangementId) {
    parameter.set("arrangementId", auswahl.arrangementId);
  }
  const zeichenkette = parameter.toString();
  const pfad = `/api/songs/${encodeURIComponent(songId)}/performances`;
  return zeichenkette ? `${pfad}?${zeichenkette}` : pfad;
}

export async function fetchLiedHistorie(
  songId: string,
  auswahl: HistorieAuswahl = {},
  signal?: AbortSignal,
): Promise<LiedHistorie> {
  const response = await fetch(historieUrlPfad(songId, auswahl), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as LiedHistorie;
}
