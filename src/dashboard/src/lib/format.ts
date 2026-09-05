export const number = new Intl.NumberFormat("de-AT", { maximumFractionDigits: 1 });
export function date(value: string): string {
	return new Intl.DateTimeFormat("de-AT", { day: "2-digit", month: "2-digit", year: "numeric", timeZone: "UTC" }).format(new Date(`${value}T12:00:00Z`));
}
export function timestamp(value: string | null): string {
	return value ? new Intl.DateTimeFormat("de-AT", { dateStyle: "short", timeStyle: "medium", timeZone: "Europe/Vienna" }).format(new Date(value)) : "Zeitpunkt unbekannt";
}
export function percent(value: number, total: number): string {
	return total > 0 ? `${number.format(value / total * 100)} %` : "–";
}
export function delta(value: number, previous: number): string {
	return previous === 0 ? (value === 0 ? "Unverändert" : "Kein Prozentvergleich (Vorperiode: 0)") : `${value > previous ? "+" : ""}${number.format((value - previous) / previous * 100)} %`;
}
export function gap(seconds: number): string {
	if (seconds < 60) return `${number.format(seconds)} Sek.`;
	if (seconds < 3600) return `${number.format(seconds / 60)} Min.`;
	return `${number.format(seconds / 3600)} Std.`;
}
