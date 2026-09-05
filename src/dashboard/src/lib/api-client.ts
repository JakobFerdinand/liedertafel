export type Range = { start: string; end: string; timezone: string };
export type Point = { bucketStart: string; count: number; partial: boolean; sessions: number; uniqueVisitors: number; uniquePaths: number; pagesPerSession: number; reloads: number };
export type VisitorPoint = { bucketStart: string; category: string; count: number; partial: boolean };
export type Stats = {
	range: Range; total: number; uniquePaths: number; sessions: number; withoutSessionId: number; pagesPerSession: number;
	uniqueVisitors: number; reloads: number; classifiedViews: number; series: Point[];
	topPaths: { path: string; count: number }[]; devices: { device: string; count: number }[];
	origins: { origin: string; count: number }[]; visitorSeries: VisitorPoint[];
};
export type StatsResponse = { range: Range; granularity: string; generatedAt: string; truncated: boolean; current: Stats; previous: Stats | null };
export type SessionSummary = {
	sessionRef: string; sessionId: string; visitorId: string; firstSeen: string | null; lastSeen: string | null;
	viewCount: number; distinctPathCount: number; entryPath: string; lastPath: string; reloadCount: number; deviceCategory: string; originHosts: string[];
};
export type SessionResponse = { range: Range; generatedAt: string; truncated: boolean; withoutSessionId: number; totalSessions: number; items: SessionSummary[]; nextCursor: string | null };
export type SessionEvent = { path: string; referrerHost: string | null; navigationType: string; viewportWidth: number; deviceCategory: string; observedAt: string | null; gapSeconds: number | null };
export type SessionDetail = { range: Range; generatedAt: string; sessionRef: string; sessionId: string; visitorId: string; truncated: boolean; possiblyTruncatedStart: boolean; possiblyTruncatedEnd: boolean; events: SessionEvent[] };
export type ErrorKind = "network" | "unauthorized" | "invalid-range" | "server" | "expired";
export class ApiError extends Error {
	constructor(public kind: ErrorKind, message: string) { super(message); }
}

type Guard = (value: unknown) => boolean;
const string: Guard = (v) => typeof v === "string";
const numeric: Guard = (v) => typeof v === "number" && Number.isFinite(v) && v >= 0;
const boolean: Guard = (v) => typeof v === "boolean";
const nullable = (guard: Guard): Guard => (v) => v === null || guard(v);
const array = (guard: Guard): Guard => (v) => Array.isArray(v) && v.every(guard);
const object = (fields: Record<string, Guard>): Guard => (v) => v !== null && typeof v === "object" && Object.entries(fields).every(([key, guard]) => guard((v as Record<string, unknown>)[key]));
const date: Guard = (v) => typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v) && Number.isFinite(Date.parse(v));
const time: Guard = (v) => typeof v === "string" && Number.isFinite(Date.parse(v));
const range = object({ start: date, end: date, timezone: (v) => v === "Europe/Vienna" });
const point = object({ bucketStart: date, count: numeric, partial: boolean, sessions: numeric, uniqueVisitors: numeric, uniquePaths: numeric, pagesPerSession: numeric, reloads: numeric });
const segment = (key: string) => object({ [key]: string, count: numeric });
const stats = object({ range, total: numeric, uniquePaths: numeric, sessions: numeric, withoutSessionId: numeric, pagesPerSession: numeric, uniqueVisitors: numeric, reloads: numeric, classifiedViews: numeric, series: array(point), topPaths: array(segment("path")), devices: array(segment("device")), origins: array(segment("origin")), visitorSeries: array(object({ bucketStart: date, category: string, count: numeric, partial: boolean })) });
export const isStats = (v: unknown): v is StatsResponse => object({ range, generatedAt: time, granularity: (g) => g === "day" || g === "week", truncated: boolean, current: stats, previous: nullable(stats) })(v);
export const isSessions = (v: unknown): v is SessionResponse => object({ range, generatedAt: time, truncated: boolean, withoutSessionId: numeric, totalSessions: numeric, nextCursor: nullable(string), items: array(object({ sessionRef: string, sessionId: string, visitorId: string, firstSeen: nullable(time), lastSeen: nullable(time), viewCount: numeric, distinctPathCount: numeric, entryPath: string, lastPath: string, reloadCount: numeric, deviceCategory: string, originHosts: array(string) })) })(v);
export const isSessionDetail = (v: unknown): v is SessionDetail => object({ range, generatedAt: time, sessionRef: string, sessionId: string, visitorId: string, truncated: boolean, possiblyTruncatedStart: boolean, possiblyTruncatedEnd: boolean, events: array(object({ path: string, referrerHost: nullable(string), navigationType: string, viewportWidth: numeric, deviceCategory: string, observedAt: nullable(time), gapSeconds: nullable(numeric) })) })(v);

export async function get<T>(url: string, guard: (v: unknown) => v is T, signal: AbortSignal): Promise<T> {
	for (let attempt = 0; ; attempt++) {
		try {
			const response = await fetch(url, { signal, cache: "no-store", headers: { Accept: "application/json" } });
			if (response.status === 401 || (response.redirected && (response.url.includes("/.auth/") || !response.headers.get("content-type")?.includes("application/json")))) {
				window.location.assign(`/.auth/login/github?post_login_redirect_uri=${encodeURIComponent(window.location.pathname + window.location.search)}`);
				throw new ApiError("unauthorized", "Bitte erneut anmelden.");
			}
			if (response.status === 403) throw new ApiError("unauthorized", "Für dieses Dashboard ist die Rolle admin oder collaborator erforderlich.");
			if (!response.ok) {
				const body = await response.json().catch(() => null);
				throw new ApiError(response.status === 400 ? "invalid-range" : response.status === 410 ? "expired" : "server", typeof body?.error === "string" ? body.error : "Daten konnten nicht geladen werden. Bitte erneut versuchen.");
			}
			const data: unknown = await response.json();
			if (!guard(data)) throw new ApiError("server", "Die Antwort hat ein unerwartetes Format. Bitte neu laden.");
			return data;
		} catch (error) {
			if (signal.aborted) throw error;
			const failure = error instanceof ApiError ? error : new ApiError("network", "Die Verbindung ist unterbrochen. Bitte erneut versuchen.");
			if (attempt === 0 && (failure.kind === "network" || failure.kind === "server")) continue;
			throw failure;
		}
	}
}
