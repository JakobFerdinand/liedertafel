<script lang="ts">
	import { onMount } from "svelte";
	import { get, isStats, type StatsResponse } from "../lib/api-client";
	import { rangeParams, sessionLink, statsParams } from "../lib/date-range";
	import { createLoader } from "../lib/loader";
	import { date, number, percent, timestamp } from "../lib/format";
	import DateRangePicker from "./filters/DateRangePicker.svelte";
	import CompareToggle from "./filters/CompareToggle.svelte";
	import KpiCard from "./overview/KpiCard.svelte";
	import TrendChart from "./overview/TrendChart.svelte";
	import BreakdownTable from "./breakdowns/BreakdownTable.svelte";
	import DeviceBars from "./breakdowns/DeviceBars.svelte";
	import VisitorSeriesChart from "./breakdowns/VisitorSeriesChart.svelte";
	const loader = createLoader<StatsResponse>({
		fetch: ({ range, signal }) => get(`/api/pageviews/stats?${statsParams(range)}`, isStats, signal),
		rewrite: (_params, range) => rangeParams(range)
	});
	let view = $state(loader.read());
	const range = $derived(view.range);
	const data = $derived(view.data);
	const loading = $derived(view.loading);
	const error = $derived(view.error);
	const shownRange = $derived(data ? { start: data.range.start, end: data.range.end, granularity: data.granularity, compare: data.previous !== null } : range);
	onMount(() => {
		const stop = loader.subscribe((next) => { view = next; });
		const begin = loader.connect();
		return () => { stop(); begin(); };
	});
</script>

<section class="section card" aria-label="Zeitraum und Vergleich">
	<DateRangePicker value={range} onchange={(value) => void loader.load(rangeParams(value))} />
	<CompareToggle checked={range.compare} onchange={(compare) => void loader.load(rangeParams({ ...range, compare }))} />
</section>
{#if loading}<p role="status" aria-live="polite">Auswertung wird geladen …</p>{/if}
{#if error}<div class="error" role="alert"><p>{error}</p><button type="button" onclick={() => void loader.load()}>Erneut versuchen</button></div>{/if}
{#if data}
	<div class:stale={loading || !!error} aria-busy={loading}>
		<p class="chart-caption">Angezeigte Daten: {date(data.range.start)} – {date(data.range.end)}. Stand: {timestamp(data.generatedAt)}.</p>
		{#if data.truncated}<p class="notice" role="status">Die Auswertung ist unvollständig: Das Lese- oder Zeitlimit wurde erreicht. Bitte den Zeitraum verkleinern. Alle Werte und Vergleiche beziehen sich nur auf die gelesenen Aufrufe.</p>{/if}
		{#if data.current.total === 0}<p class="empty">Keine Seitenaufrufe im ausgewählten Zeitraum. Wähle oben einen anderen Zeitraum.</p>{/if}
		<div class="section-grid">
			<KpiCard label="Seitenaufrufe" value={data.current.total} previous={data.previous?.total} values={data.current.series.map((p) => p.count)} description="Erfasste Seitenaufrufe im Zeitraum." primary />
			<KpiCard label="Sitzungs-Kennungen" value={data.current.sessions} previous={data.previous?.sessions} values={data.current.series.map((p) => p.sessions)} description="Unterschiedliche, nicht leere Sitzungs-IDs im Zeitraum. Kein Inaktivitätslimit." />
			<KpiCard label="Besucher-Kennungen" value={data.current.uniqueVisitors} previous={data.previous?.uniqueVisitors} values={data.current.series.map((p) => p.uniqueVisitors)} description="Unterschiedliche, nicht leere Besucher-IDs im Zeitraum; keine Personenzählung." />
			<KpiCard label="Unterschiedliche Seiten" value={data.current.uniquePaths} previous={data.previous?.uniquePaths} values={data.current.series.map((p) => p.uniquePaths)} description="Unterschiedliche erfasste Seitenpfade." />
			<KpiCard label="Aufrufe pro Sitzungs-ID" value={data.current.pagesPerSession} previous={data.previous?.pagesPerSession} values={data.current.series.map((p) => p.pagesPerSession)} description="Aufrufe mit Sitzungs-ID geteilt durch unterschiedliche Sitzungs-IDs." />
			<KpiCard label="Reloads" value={data.current.reloads} previous={data.previous?.reloads} values={data.current.series.map((p) => p.reloads)} description={`${percent(data.current.reloads, data.current.total)} aller Aufrufe; ${percent(data.current.reloads, data.current.classifiedViews)} aller klassifizierten Aufrufe.`} />
		</div>
		<p class="chart-caption">{number.format(data.current.withoutSessionId)} Aufrufe ohne Sitzungs-ID sind in der Statistik enthalten und fehlen in der Sitzungsliste. Sparklines zeigen Werte je Abschnitt; Kennungen können in mehreren Abschnitten vorkommen.</p>
		<p><a href={sessionLink(shownRange)}>Sitzungen in diesem Zeitraum untersuchen</a></p>
		<TrendChart current={data.current} previous={data.previous} range={shownRange} />
		<div class="breakdowns">
			<BreakdownTable title="Meistbesuchte Seiten" rows={data.current.topPaths.map((p) => ({ label: p.path, count: p.count }))} filter="path" range={shownRange} description="Die zehn häufigsten Seiten. Eine Seite auswählen, um passende Sitzungen zu sehen." />
			<BreakdownTable title="Externe Herkunft" rows={data.current.origins.map((o) => ({ label: o.origin, count: o.count }))} filter="originHost" range={shownRange} description="Die sechs häufigsten externen Herkunfts-Domains. Interne und fehlende Referrer sind ausgeschlossen." />
			<DeviceBars devices={data.current.devices} range={shownRange} />
			<VisitorSeriesChart series={data.current.visitorSeries} range={shownRange} />
		</div>
	</div>
{/if}
<style>
	.breakdowns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; }
	@media (max-width: 760px) { .breakdowns { grid-template-columns: 1fr; } }
</style>
