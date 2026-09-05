export type RangeState = { start: string; end: string; granularity: string; compare: boolean };
export const timezone = "Europe/Vienna";
export const dailyMaximum = 92;
export function today(): string {
	const parts = new Intl.DateTimeFormat("en-CA", { timeZone: timezone, year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(new Date());
	return ["year", "month", "day"].map((key) => parts.find((p) => p.type === key)!.value).join("-");
}
export function addDays(date: string, days: number): string {
	const value = new Date(`${date}T12:00:00Z`);
	value.setUTCDate(value.getUTCDate() + days);
	return value.toISOString().slice(0, 10);
}
export function addMonths(date: string, months: number): string {
	const value = new Date(`${date}T12:00:00Z`);
	const day = value.getUTCDate();
	value.setUTCDate(1);
	value.setUTCMonth(value.getUTCMonth() + months);
	const last = new Date(Date.UTC(value.getUTCFullYear(), value.getUTCMonth() + 1, 0)).getUTCDate();
	value.setUTCDate(Math.min(day, last));
	return value.toISOString().slice(0, 10);
}
export function span(range: Pick<RangeState, "start" | "end">): number {
	return (Date.parse(range.end) - Date.parse(range.start)) / 86400000 + 1;
}
export function defaultRange(): RangeState {
	const end = today();
	return { start: addDays(end, -6), end, granularity: "day", compare: true };
}
export function readRange(params: URLSearchParams): RangeState {
	const fallback = defaultRange();
	return { start: params.get("start") ?? fallback.start, end: params.get("end") ?? fallback.end, granularity: params.get("granularity") ?? "day", compare: params.get("compare") !== "0" };
}
export function rangeParams(range: RangeState): URLSearchParams {
	return new URLSearchParams({ start: range.start, end: range.end, granularity: range.granularity, compare: range.compare ? "1" : "0" });
}
export function statsParams(range: RangeState): URLSearchParams {
	const params = rangeParams(range);
	params.set("compare", range.compare ? "previous_period" : "none");
	return params;
}
export function sessionLink(range: RangeState, filters: Record<string, string> = {}): string {
	const params = rangeParams(range);
	for (const [key, value] of Object.entries(filters)) params.set(key, value);
	return `/sessions?${params}`;
}
export function writeUrl(params: URLSearchParams): void {
	window.history.replaceState(null, "", `${window.location.pathname}?${params}`);
}
export function validDate(value: string): boolean {
	return /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(value)) && new Date(`${value}T12:00:00Z`).toISOString().slice(0, 10) === value;
}
export function rangeError(range: RangeState, sessions = false): string | null {
	if (!validDate(range.start) || !validDate(range.end)) return "Bitte Start und Ende als gültiges Datum eingeben.";
	if (range.end < range.start) return "Das Ende darf nicht vor dem Start liegen.";
	if (range.start < addMonths(today(), -36) || range.end > today()) return "Der Zeitraum muss innerhalb der letzten 36 Monate bis heute liegen.";
	if (!["day", "week"].includes(range.granularity)) return "Bitte Tag oder Woche als Auflösung wählen.";
	const maximum = sessions || range.granularity === "day" ? 92 : 400;
	if (span(range) > maximum) return `Bitte den Zeitraum auf höchstens ${maximum} Tage begrenzen${sessions ? ", um einzelne Sitzungen zu untersuchen" : ""}.`;
	if (!sessions && range.compare && addDays(range.start, -span(range)) < addMonths(today(), -36)) return "Die Vorperiode liegt außerhalb der letzten 36 Monate. Bitte den Vergleich ausschalten.";
	return null;
}
