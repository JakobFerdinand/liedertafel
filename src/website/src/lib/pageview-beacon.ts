// Pageview-Beacon: baut das Payload für POST /api/pageview zusammen und
// sendet es einmal beim Verlassen der Seite. Feldnamen und Werte decken mit
// dem Empfänger in src/website-api/features/pageviews/PageView.cs überein.

export const SESSION_ID_KEY = "lt-session";
export const VISITOR_ID_KEY = "lt-visitor";

const productionHosts = ["liedertafel-mining.at", "www.liedertafel-mining.at"];

export type IdStorage = Pick<Storage, "getItem" | "setItem">;

export type PageviewPayload = {
	path: string;
	referrerHost: string;
	viewportWidth: number;
	sessionId: string;
	visitorId: string;
	navigationType: string;
};

export function trimPath(path: string): string {
	let trimmed = path;
	if (trimmed.length > 1 && trimmed.endsWith("/")) {
		trimmed = trimmed.slice(0, -1);
	}
	return trimmed;
}

export function referrerHostOf(referrer: string): string {
	try {
		return new URL(referrer).host;
	} catch {
		return "";
	}
}

// Blockierter Speicher wirft beim Lesen oder Schreiben; jede Störung ergibt –
// wie bisher – eine leere Id statt eines Abbruchs.
export function readOrCreateId(
	storage: IdStorage | undefined,
	key: string,
	create: () => string,
): string {
	if (!storage) {
		return "";
	}
	try {
		const existing = storage.getItem(key);
		if (existing) {
			return existing;
		}
		const created = create();
		storage.setItem(key, created);
		return created;
	} catch {
		return "";
	}
}

export function navigationTypeFrom(entries: { type?: string }[]): string {
	const first = entries[0];
	if (first?.type) {
		return first.type;
	}
	return "navigate";
}

export function isProductionHost(hostname: string): boolean {
	return productionHosts.includes(hostname);
}

// window.sessionStorage/localStorage wirft bei blockierten Cookies schon beim Zugriff.
export function tryStorage(access: () => IdStorage | undefined): IdStorage | undefined {
	try {
		return access();
	} catch {
		return undefined;
	}
}

export function buildPageviewPayload(input: {
	pathname: string;
	referrer: string;
	viewportWidth: number;
	sessionStorage?: IdStorage;
	localStorage?: IdStorage;
	navigationEntries?: { type?: string }[];
	createId?: () => string;
}): PageviewPayload {
	const createId = input.createId ?? ((): string => crypto.randomUUID());
	return {
		path: trimPath(input.pathname),
		referrerHost: referrerHostOf(input.referrer),
		viewportWidth: input.viewportWidth,
		sessionId: readOrCreateId(input.sessionStorage, SESSION_ID_KEY, createId),
		visitorId: readOrCreateId(input.localStorage, VISITOR_ID_KEY, createId),
		navigationType: navigationTypeFrom(input.navigationEntries ?? []),
	};
}

export function pageviewBeaconBody(payload: PageviewPayload): Blob {
	return new Blob([JSON.stringify(payload)], { type: "application/json" });
}

// Läuft nach dem DOM-Aufbau: Payload einmal zusammenstellen, erst beim
// Verlassen der Seite senden (pagehide und hidden via visibilitychange,
// abgesichert gegen Doppelversand).
export function startPageviewBeacon(): void {
	if (!isProductionHost(location.hostname) || !("sendBeacon" in navigator)) {
		return;
	}

	const payload = buildPageviewPayload({
		pathname: location.pathname,
		referrer: document.referrer,
		viewportWidth: screen.width,
		sessionStorage: tryStorage(() => sessionStorage),
		localStorage: tryStorage(() => localStorage),
		navigationEntries: performance.getEntriesByType("navigation") as PerformanceNavigationTiming[],
	});

	let sent = false;
	const send = () => {
		if (sent) {
			return;
		}
		sent = true;
		navigator.sendBeacon("/api/pageview", pageviewBeaconBody(payload));
	};
	window.addEventListener("pagehide", send);
	document.addEventListener("visibilitychange", () => {
		if (document.visibilityState === "hidden") {
			send();
		}
	});
}
