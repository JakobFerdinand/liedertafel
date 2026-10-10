// Events: dates are calendar days ("yyyy-mm-dd") without a time zone.
// ISO day strings sort and compare correctly as plain strings.

type EventDateStyle = "short" | "long";

const formatters: Record<EventDateStyle, Intl.DateTimeFormat> = {
	short: new Intl.DateTimeFormat("de-AT", {
		weekday: "short",
		year: "numeric",
		month: "long",
		day: "numeric",
		timeZone: "UTC",
	}),
	long: new Intl.DateTimeFormat("de-AT", {
		weekday: "long",
		day: "numeric",
		month: "long",
		year: "numeric",
		timeZone: "UTC",
	}),
};

const pdfFiles = import.meta.glob("../content/events/assets/*.pdf", {
	eager: true,
	query: "?url",
	import: "default",
}) as Record<string, string>;

// Calendar day of `now` in local time. On this static site it is the build day.
export function today(now: Date = new Date()): string {
	const month = String(now.getMonth() + 1).padStart(2, "0");
	const day = String(now.getDate()).padStart(2, "0");
	return `${now.getFullYear()}-${month}-${day}`;
}

export function formatEventDate(date: string, style: EventDateStyle): string {
	const [year, month, day] = date.split("-").map(Number);
	return formatters[style].format(new Date(Date.UTC(year, month - 1, day)));
}

// Events on or after `todayDate`, earliest first.
export function selectUpcoming<T extends { data: { date: string } }>(entries: T[], todayDate: string): T[] {
	return entries
		.filter((entry) => entry.data.date >= todayDate)
		.sort((a, b) => (a.data.date < b.data.date ? -1 : a.data.date > b.data.date ? 1 : 0));
}

// Public URL of the event's PDF, or undefined without one. Throws if it is missing.
export function resolveEventPdf(
	entry: { id: string; data: { pdf?: string } },
	files: Record<string, string> = pdfFiles,
): string | undefined {
	const { pdf } = entry.data;
	if (!pdf) return undefined;

	const url = files[`../content/events/${pdf.replace(/^\.\//, "")}`];
	if (!url) {
		throw new Error(`PDF für Termin "${entry.id}" wurde nicht gefunden: ${pdf}`);
	}
	return url;
}
