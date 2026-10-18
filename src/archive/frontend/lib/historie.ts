// ARC-031 / #170: Aufführungsgeschichte eines Liedes. Die Zeilen sind die
// Aufführungsnachweise (ARC-028) mit ihrer stabilen Id — daran hängt die
// Aufnahmenverknüpfung (ARC-032). Die Zahlen sind ehrlich begrenzt: sie
// beschreiben nur die erfasste Überlieferung, bestätigte Aufführungen und
// unbestätigte Programmangaben bleiben getrennt, mögliche Doppelerfassungen
// werden gekennzeichnet statt zusammengeführt.

import type { HistorieAufnahme } from "@/lib/zeitmarken";

export type HistorieLied = {
  id: string;
  title: string;
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
  // Stelle unter den bestätigten Aufführungen des Liedes an diesem Auftritt.
  occurrence: { index: number; of: number } | null;
  possiblyDuplicate: boolean;
  // Auf jeder bestätigten Zeile eines Auftritts, an dem eine bestätigte
  // Zeile möglicherweise doppelt erfasst ist: die Anzahl dort ist eine
  // Obergrenze, keine gesicherte Wiederholung.
  possiblyDuplicateAtEvent: boolean;
  alsoConfirmedAtEvent: boolean;
  // Aufnahmen mit einer markierten Stelle dieser Aufführung (ARC-032); leer,
  // wo nichts markiert ist — kein Beleg dafür, dass es keine Aufnahme gibt.
  recordings: HistorieAufnahme[];
  // Nur für die Redaktion.
  sourceNote?: string | null;
};

export type LiedHistorie = {
  lied: HistorieLied;
  page: number;
  pageSize: number;
  total: number;
  counts: HistorieZaehlung;
  performances: HistorieZeile[];
};

export type HistorieAuswahl = {
  page?: number;
  // "confirmed" | "mention"; leer = alle Nachweise.
  evidence?: string;
};

export function historieUrlPfad(
  liedId: string,
  auswahl: HistorieAuswahl = {},
): string {
  const parameter = new URLSearchParams();
  if (auswahl.page && auswahl.page > 1) {
    parameter.set("page", String(auswahl.page));
  }
  if (auswahl.evidence) parameter.set("evidence", auswahl.evidence);
  const zeichenkette = parameter.toString();
  const pfad = `/api/lieder/${encodeURIComponent(liedId)}/performances`;
  return zeichenkette ? `${pfad}?${zeichenkette}` : pfad;
}

export async function fetchLiedHistorie(
  liedId: string,
  auswahl: HistorieAuswahl = {},
  signal?: AbortSignal,
): Promise<LiedHistorie> {
  const response = await fetch(historieUrlPfad(liedId, auswahl), {
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  if (!response.ok) throw response;
  return (await response.json()) as LiedHistorie;
}
