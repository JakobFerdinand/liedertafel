<script lang="ts">
	import { Area, Axis, Chart, Spline, Text, type TextProps } from "layerchart";
	import { scaleLinear } from "d3-scale";
	import type { Stats } from "../../lib/api-client";
	import { addDays, sessionLink, type RangeState } from "../../lib/date-range";
	import { date, number } from "../../lib/format";
	let { current, previous, range }: { current: Stats; previous: Stats | null; range: RangeState } = $props();
	let zoomStart = $state(0);
	let zoomEnd = $state(0);
	const length = $derived(Math.max(current.series.length, previous?.series.length ?? 0));
	$effect(() => { current; previous; zoomStart = 0; zoomEnd = length - 1; });
	const points = $derived(Array.from({ length }, (_, index) => ({ index, current: current.series[index]?.count ?? null, previous: previous?.series[index]?.count ?? null })).slice(zoomStart, zoomEnd + 1));
	const maximum = $derived(Math.max(1, ...points.flatMap((p) => [p.current ?? 0, p.previous ?? 0])));
	function bucketLink(index: number) {
		const bucket = current.series[index].bucketStart;
		return sessionLink({ ...range, start: bucket < range.start ? range.start : bucket, end: [addDays(bucket, range.granularity === "day" ? 0 : 6), range.end].sort()[0] });
	}
</script>
<section class="section card" aria-labelledby="trend-heading">
	<h2 id="trend-heading">Aufrufe im Zeitverlauf</h2>
	<p class="chart-caption">{date(current.range.start)} – {date(current.range.end)}{previous ? `; Vorperiode: ${date(previous.range.start)} – ${date(previous.range.end)}` : ""}</p>
	<div class="legend"><span><i class="current"></i>Ausgewählter Zeitraum</span>{#if previous}<span><i class="previous"></i>Vorperiode (gestrichelt)</span>{/if}</div>
	{#snippet countLabel({ props }: { props: TextProps; index: number })}<Text {...props} value={number.format(Number(props.value ?? 0))} />{/snippet}
	{#snippet dateLabel({ props }: { props: TextProps; index: number })}<Text {...props} value={current.series[Number(props.value)] ? date(current.series[Number(props.value)].bucketStart).slice(0, 6) : ""} />{/snippet}
	<div class="trend" aria-hidden="true">
		<Chart data={points} x="index" y="current" xScale={scaleLinear()} xDomain={zoomStart === zoomEnd ? [zoomStart - 0.5, zoomEnd + 0.5] : [zoomStart, zoomEnd]} yDomain={[0, maximum]} height={280} padding={{ top: 16, right: 20, bottom: 36, left: 48 }}>
			{#snippet axis()}
				<Axis placement="left" tickLabel={countLabel} tickMarks={false} fill="var(--color-text-muted)" stroke="var(--color-border)" />
				<Axis placement="bottom" ticks={points.filter((_, i) => i % Math.max(1, Math.ceil(points.length / 7)) === 0).map((p) => p.index)} tickLabel={dateLabel} tickMarks={false} fill="var(--color-text-muted)" stroke="var(--color-border)" />
			{/snippet}
			{#snippet marks({ context })}
				<Area fill="var(--color-accent-soft)" defined={(p: { current: number | null }) => p.current !== null} />
				<Spline stroke="var(--color-brand)" strokeWidth={2.5} defined={(p: { current: number | null }) => p.current !== null} />
				{#if previous}<Spline y="previous" stroke="var(--chart-path-2)" strokeWidth={2} stroke-dasharray="6 5" defined={(p: { previous: number | null }) => p.previous !== null} />{/if}
				{#each points as point}
					{#if point.current !== null}<circle cx={context.xScale(point.index)} cy={context.yScale(point.current)} r={current.series[point.index]?.partial ? 5 : 3} fill={current.series[point.index]?.partial ? "var(--color-surface)" : "var(--color-brand)"} stroke="var(--color-brand)" stroke-width="2" />{/if}
				{/each}
			{/snippet}
		</Chart>
	</div>
	{#if length > 2}
		<div class="zoom">
			<label>Zoom ab Abschnitt {zoomStart + 1}<input type="range" min="0" max={zoomEnd} bind:value={zoomStart} /></label>
			<label>Zoom bis Abschnitt {zoomEnd + 1}<input type="range" min={zoomStart} max={length - 1} bind:value={zoomEnd} /></label>
			<button type="button" onclick={() => { zoomStart = 0; zoomEnd = length - 1; }}>Gesamten Verlauf zeigen</button>
		</div>
	{/if}
	{#if current.series.some((p) => p.partial)}<p class="notice">Offene Kreise: unvollständige Abschnitte. Heute kommen noch Aufrufe hinzu; Wochen an den Zeitraumgrenzen können verkürzt sein.</p>{/if}
	{#if previous && range.granularity === "week"}<p class="chart-caption">Wochen werden der Reihe nach verglichen. An den Grenzen können die Wochen unterschiedlich viele ausgewählte Tage enthalten.</p>{/if}
	<details>
		<summary>Werte und Sitzungen je {range.granularity === "day" ? "Tag" : "Woche"} anzeigen</summary>
		<div class="table-scroll"><table class="table">
			<thead><tr><th>Abschnitt ab</th><th>Aufrufe</th>{#if previous}<th>Vorperiode ab</th><th>Aufrufe Vorperiode</th>{/if}</tr></thead>
			<tbody>{#each points as point}<tr>
				<td>{#if current.series[point.index]}<a href={bucketLink(point.index)}>{date(current.series[point.index].bucketStart)}</a>{current.series[point.index].partial ? " (unvollständig)" : ""}{:else}–{/if}</td>
				<td>{point.current === null ? "–" : number.format(point.current)}</td>
				{#if previous}<td>{previous.series[point.index] ? date(previous.series[point.index].bucketStart) : "–"}</td><td>{point.previous === null ? "–" : number.format(point.previous)}</td>{/if}
			</tr>{/each}</tbody>
		</table></div>
	</details>
</section>
<style>
	.current { background: var(--color-brand); }
	.previous { background: var(--chart-path-2); }
	.trend { margin: 1rem -0.5rem; }
	.zoom { display: flex; flex-wrap: wrap; gap: 1rem; align-items: end; }
	.zoom label { flex: 1; min-width: 9rem; }
</style>
