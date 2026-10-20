// Wann ein Lied gesungen wurde (#160): abgeleitet aus den Programmen der
// Auftritte und aus Lied-Aufnahmen, die einen Auftritt nennen. Eine Zeile
// ist ein Auftritt. Die Zahlen beschreiben nur, was das Archiv bisher
// dokumentiert hat; unsichere Daten bleiben gekennzeichnet.

import type { HistorieAufnahme } from "@/lib/zeitmarken";

export type HistorieLied = {
  id: string;
  title: string;
};

export type HistorieZaehlung = {
  // Veröffentlichte Auftritte in der Historie des Liedes.
  events: number;
  // Davon mit dem Lied im Programm (sonst nur eine Lied-Aufnahme).
  inProgramme: number;
  uncertainDates: number;
  // Nur für die Redaktion: unveröffentlichte Auftritte, die in keiner Zahl
  // stehen. Mitglieder erhalten null.
  draftEvents: number | null;
};

// Aufnahme des Liedes, die diesen Auftritt nennt.
export type HistorieLiedAufnahme = {
  id: string;
  fileName: string | null;
  contentType: string;
  description: string | null;
};

export type HistorieZeile = {
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
  inProgramme: boolean;
  // Wie oft das Lied im Programm dieses Auftritts steht.
  entries: number;
  // Aufnahmen mit einer Zeitmarke dieses Liedes; leer, wo nichts markiert
  // ist — kein Beleg dafür, dass es keine Aufnahme gibt.
  recordings: HistorieAufnahme[];
  liedAufnahmen: HistorieLiedAufnahme[];
};

export type LiedHistorie = {
  lied: HistorieLied;
  page: number;
  pageSize: number;
  total: number;
  counts: HistorieZaehlung;
  auftritte: HistorieZeile[];
};

export type HistorieAuswahl = {
  page?: number;
};

export function historieUrlPfad(
  liedId: string,
  auswahl: HistorieAuswahl = {},
): string {
  const parameter = new URLSearchParams();
  if (auswahl.page && auswahl.page > 1) {
    parameter.set("page", String(auswahl.page));
  }
  const zeichenkette = parameter.toString();
  const pfad = `/api/lieder/${encodeURIComponent(liedId)}/historie`;
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
