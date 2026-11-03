import { ApiError, type ErrorKind } from "./api-client.ts";
import { defaultRange, rangeError, rangeParams, readRange, writeUrl, type RangeState } from "./date-range.ts";

/**
 * The shared orchestration behind the dashboard's islands: one load at a time
 * (the loser aborts), URL state till fetch, pre-flight validation, stale and
 * error marking, the popstate restore and the abort on cleanup. The
 * island-specific business — which endpoint to call and how to merge the
 * result — is injected as `fetch`; everything around it lives here, so the
 * lib tests can drive it without a browser.
 */
export type LoaderRequest<T> = { params: URLSearchParams; range: RangeState; previous: T | null; append: boolean; signal: AbortSignal };

export type LoaderWindow = {
	readonly search: string;
	addEventListener: (type: "popstate", listener: () => void) => void;
	removeEventListener: (type: "popstate", listener: () => void) => void;
};

export type LoaderConfig<T> = {
	/** Serves the content of one load; deprecated by the loader when a newer load takes over. */
	fetch: (request: LoaderRequest<T>) => Promise<T>;
	/** Where the ambient URL and popstate come from; omitted in the islands, injected in tests. */
	window?: LoaderWindow;
	/** Persists the current state in the URL; defaults to date-range's writeUrl. */
	write?: (params: URLSearchParams) => void;
	/** Shapes the URL contents; defaults to the merged parameters (the sessions island). */
	rewrite?: (params: URLSearchParams, range: RangeState) => URLSearchParams;
	/** Pre-flight wording; defaults to date-range's rangeError. */
	validate?: (range: RangeState) => string | null;
	/** Message for a failure that is no Error; used verbatim by the sessions island. */
	fallback?: string;
};

export type LoaderView<T> = {
	range: RangeState;
	params: URLSearchParams;
	data: T | null;
	/** The parameters behind the shown data, frozen at its success. */
	shown: URLSearchParams;
	loading: boolean;
	error: string | null;
	failed: ErrorKind | null;
	/** loading or a failure: the shown data is not current. */
	stale: boolean;
};

export type Loader<T> = {
	read(): LoaderView<T>;
	subscribe(listener: (view: LoaderView<T>) => void): () => void;
	load(next?: URLSearchParams, append?: boolean): Promise<void>;
	connect(): () => void;
};

const defaultFallback = "Daten konnten nicht geladen werden.";

function domWindow(): LoaderWindow {
	return {
		get search() { return window.location.search; },
		addEventListener: (type, listener) => window.addEventListener(type, listener),
		removeEventListener: (type, listener) => window.removeEventListener(type, listener)
	};
}

export function createLoader<T>(config: LoaderConfig<T>): Loader<T> {
	const rewrite = config.rewrite ?? ((source: URLSearchParams) => source);
	const validate = config.validate ?? rangeError;
	const persist = config.write ?? writeUrl;
	const fallback = config.fallback ?? defaultFallback;
	const listeners = new Set<(view: LoaderView<T>) => void>();
	let params = new URLSearchParams();
	let range = defaultRange();
	let data: T | null = null;
	let shown = new URLSearchParams();
	let loading = false;
	let error: string | null = null;
	let failed: ErrorKind | null = null;
	let active: AbortController | undefined;
	const emit = () => {
		const view: LoaderView<T> = { range, params, data, shown, loading, error, failed, stale: loading || error !== null };
		for (const listener of [...listeners]) listener(view);
	};

	async function load(next?: URLSearchParams, append = false) {
		active?.abort();
		const controller = new AbortController(); active = controller;
		params = new URLSearchParams(next ?? params);
		range = readRange(params);
		for (const [key, value] of rangeParams(range)) params.set(key, value);
		persist(rewrite(params, range));
		error = validate(range) ?? null;
		failed = null; loading = false;
		emit();
		if (error) return;
		loading = true;
		emit();
		try {
			const result = await config.fetch({ params, range, previous: data, append, signal: controller.signal });
			if (controller.signal.aborted) return;
			data = result;
			shown = new URLSearchParams(params);
		}
		catch (cause) {
			if (controller.signal.aborted) return;
			error = cause instanceof Error ? cause.message : fallback;
			failed = cause instanceof ApiError ? cause.kind : null;
		}
		finally { if (!controller.signal.aborted) { loading = false; emit(); } }
	}

	function read(): LoaderView<T> {
		return { range, params, data, shown, loading, error, failed, stale: loading || error !== null };
	}

	function subscribe(listener: (view: LoaderView<T>) => void): () => void {
		listeners.add(listener);
		return () => { listeners.delete(listener); };
	}

	function connect(): () => void {
		const ambient = config.window ?? domWindow();
		void load(new URLSearchParams(ambient.search));
		const restore = () => void load(new URLSearchParams(ambient.search));
		ambient.addEventListener("popstate", restore);
		return () => { active?.abort(); ambient.removeEventListener("popstate", restore); };
	}

	return { read, subscribe, load, connect };
}
