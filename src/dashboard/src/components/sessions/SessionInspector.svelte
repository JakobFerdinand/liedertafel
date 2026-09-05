<script lang="ts">
	import { onMount } from "svelte";
	import { ApiError, get, isSessionDetail, isSessions, type SessionDetail, type SessionResponse } from "../../lib/api-client";
	import { defaultRange, rangeError, rangeParams, readRange, writeUrl, type RangeState } from "../../lib/date-range";
	import DateRangePicker from "../filters/DateRangePicker.svelte";
	import SessionFilters from "./SessionFilters.svelte";
	import SessionList from "./SessionList.svelte";
	import SessionTimeline from "./SessionTimeline.svelte";
	let range = $state(defaultRange());
	let params = $state(new URLSearchParams());
	let shownParams = $state(new URLSearchParams());
	let list = $state<SessionResponse | null>(null);
	let detail = $state<SessionDetail | null>(null);
	let loading = $state(false);
	let error = $state("");
	let expired = $state(false);
	let active: AbortController | undefined;
	async function load(next: URLSearchParams, append = false) {
		active?.abort(); const controller = new AbortController(); active = controller;
		params = new URLSearchParams(next); range = readRange(params);
		for (const [key, value] of rangeParams(range)) params.set(key, value);
		writeUrl(params); error = rangeError(range, true) ?? ""; expired = false; loading = false;
		if (error) return;
		loading = true;
		const session = params.get("session");
		try {
			if (session) {
				const query = new URLSearchParams({ start: range.start, end: range.end });
				const result = await get(`/api/pageviews/sessions/${encodeURIComponent(session)}?${query}`, isSessionDetail, controller.signal);
				if (controller.signal.aborted) return;
				detail = result; list = null;
			} else {
				const result = await get(`/api/pageviews/sessions?${params}`, isSessions, controller.signal);
				if (controller.signal.aborted) return;
				list = append && list ? { ...result, items: [...list.items, ...result.items] } : result; detail = null;
			}
			shownParams = new URLSearchParams(params);
		} catch (failure) {
			if (!controller.signal.aborted) { error = failure instanceof Error ? failure.message : "Sitzungen konnten nicht geladen werden."; expired = failure instanceof ApiError && failure.kind === "expired"; }
		} finally { if (!controller.signal.aborted) loading = false; }
	}
	function changeRange(value: RangeState) {
		const next = new URLSearchParams(params);
		for (const [key, val] of rangeParams(value)) next.set(key, val);
		next.delete("cursor"); next.delete("session"); void load(next);
	}
	function changeFilters(filters: URLSearchParams) {
		const next = rangeParams(range);
		for (const [key, value] of filters) next.set(key, value);
		void load(next);
	}
	function restart() { const next = new URLSearchParams(params); next.delete("cursor"); next.delete("session"); void load(next); }
	function backLink() { const next = new URLSearchParams(params); next.delete("session"); next.delete("cursor"); return `/sessions?${next}`; }
	onMount(() => {
		void load(new URLSearchParams(window.location.search));
		const restore = () => void load(new URLSearchParams(window.location.search));
		window.addEventListener("popstate", restore);
		return () => { active?.abort(); window.removeEventListener("popstate", restore); };
	});
</script>
<section class="section card" aria-label="Sitzungssuche">
	<DateRangePicker value={range} onchange={changeRange} sessions />
	<SessionFilters value={params} onchange={changeFilters} />
</section>
<p class="notice">Eine Sitzungs-Kennung stammt aus einem Browser-Tab, wird vom Browser gemeldet und hat kein Inaktivitätslimit. Ein offener Tab kann über Tage dieselbe Kennung behalten. Angezeigt werden ausschließlich die Aufrufe im Suchzeitraum; Zeitabstände messen keine Verweildauer.</p>
{#if params.has("session")}<p><a href={backLink()}>Zur gefilterten Sitzungsliste</a></p>{/if}
{#if loading}<p role="status" aria-live="polite">Sitzungsdaten werden geladen …</p>{/if}
{#if error}<div class="error" role="alert"><p>{error}</p><button type="button" onclick={() => expired ? restart() : void load(params)}>{expired ? "Suche neu starten" : "Erneut versuchen"}</button></div>{/if}
<div class:stale={loading || !!error} aria-busy={loading}>
	{#if detail}<SessionTimeline data={detail} />{/if}
	{#if list}<SessionList data={list} params={shownParams} {loading} onnext={() => { if (list?.nextCursor) { const next = new URLSearchParams(params); next.set("cursor", list.nextCursor); void load(next, true); } }} />{/if}
</div>
