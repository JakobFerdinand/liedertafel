<script lang="ts">
	import { onMount } from "svelte";
	import { get, isSessionDetail, isSessions, type SessionDetail, type SessionResponse } from "../../lib/api-client";
	import { rangeError, rangeParams, type RangeState } from "../../lib/date-range";
	import { createLoader } from "../../lib/loader";
	import DateRangePicker from "../filters/DateRangePicker.svelte";
	import SessionFilters from "./SessionFilters.svelte";
	import SessionList from "./SessionList.svelte";
	import SessionTimeline from "./SessionTimeline.svelte";
	type SessionData = { list: SessionResponse | null; detail: SessionDetail | null };
	const loader = createLoader<SessionData>({
		fetch: async ({ params, range, previous, append, signal }) => {
			const session = params.get("session");
			if (session) {
				const query = new URLSearchParams({ start: range.start, end: range.end });
				const detail = await get(`/api/pageviews/sessions/${encodeURIComponent(session)}?${query}`, isSessionDetail, signal);
				return { list: null, detail };
			}
			const result = await get(`/api/pageviews/sessions?${params}`, isSessions, signal);
			return { list: append && previous?.list ? { ...result, items: [...previous.list.items, ...result.items] } : result, detail: null };
		},
		validate: (range) => rangeError(range, true),
		fallback: "Sitzungen konnten nicht geladen werden."
	});
	let view = $state(loader.read());
	const range = $derived(view.range);
	const params = $derived(view.params);
	const data = $derived(view.data);
	const list = $derived(data?.list ?? null);
	const detail = $derived(data?.detail ?? null);
	const shownParams = $derived(view.shown);
	const loading = $derived(view.loading);
	const error = $derived(view.error);
	const expired = $derived(view.failed === "expired");
	function changeRange(value: RangeState) {
		const next = new URLSearchParams(params);
		for (const [key, val] of rangeParams(value)) next.set(key, val);
		next.delete("cursor"); next.delete("session"); void loader.load(next);
	}
	function changeFilters(filters: URLSearchParams) {
		const next = rangeParams(range);
		for (const [key, value] of filters) next.set(key, value);
		void loader.load(next);
	}
	function restart() { const next = new URLSearchParams(params); next.delete("cursor"); next.delete("session"); void loader.load(next); }
	function backLink() { const next = new URLSearchParams(params); next.delete("session"); next.delete("cursor"); return `/sessions?${next}`; }
	onMount(() => {
		const stop = loader.subscribe((next) => { view = next; });
		const begin = loader.connect();
		return () => { stop(); begin(); };
	});
</script>
<section class="section card" aria-label="Sitzungssuche">
	<DateRangePicker value={range} onchange={changeRange} sessions />
	<SessionFilters value={params} onchange={changeFilters} />
</section>
<p class="notice">Eine Sitzungs-Kennung stammt aus einem Browser-Tab, wird vom Browser gemeldet und hat kein Inaktivitätslimit. Ein offener Tab kann über Tage dieselbe Kennung behalten. Angezeigt werden ausschließlich die Aufrufe im Suchzeitraum; Zeitabstände messen keine Verweildauer.</p>
{#if params.has("session")}<p><a href={backLink()}>Zur gefilterten Sitzungsliste</a></p>{/if}
{#if loading}<p role="status" aria-live="polite">Sitzungsdaten werden geladen …</p>{/if}
{#if error}<div class="error" role="alert"><p>{error}</p><button type="button" onclick={() => expired ? restart() : void loader.load(params)}>{expired ? "Suche neu starten" : "Erneut versuchen"}</button></div>{/if}
<div class:stale={loading || !!error} aria-busy={loading}>
	{#if detail}<SessionTimeline data={detail} />{/if}
	{#if list}<SessionList data={list} params={shownParams} {loading} onnext={() => { if (list?.nextCursor) { const next = new URLSearchParams(params); next.set("cursor", list.nextCursor); void loader.load(next, true); } }} />{/if}
</div>
