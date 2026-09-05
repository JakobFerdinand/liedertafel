<script lang="ts">
	import { Chart, Spline } from "layerchart";
	import { scaleLinear } from "d3-scale";
	import type { VisitorPoint } from "../../lib/api-client";
	import { addDays, sessionLink, type RangeState } from "../../lib/date-range";
	import { date, number } from "../../lib/format";
	let { series, range }: { series: VisitorPoint[]; range: RangeState } = $props();
	const buckets = $derived([...new Set(series.map((p) => p.bucketStart))]);
	const points = $derived(buckets.map((bucketStart, index) => ({ index, bucketStart, fresh: series.find((p) => p.bucketStart === bucketStart && p.category === "Neu in diesem Zeitraum")?.count ?? 0, returning: series.find((p) => p.bucketStart === bucketStart && p.category === "Bereits zuvor im Zeitraum gesehen")?.count ?? 0 })));
	const maximum = $derived(Math.max(1, ...points.flatMap((p) => [p.fresh, p.returning])));
</script>
<section class="card">
	<h2>Besucher-Kennungen im Zeitraum</h2>
	<p class="chart-caption">Je Abschnitt einmal gezählt. „Neu“ bedeutet erstmals in diesem Zeitraum gesehen; Personen oder Besuche lassen sich daraus nicht bestimmen.</p>
	<div class="legend"><span><i style:background="var(--color-brand)"></i>Neu in diesem Zeitraum</span><span><i style:background="var(--chart-path-2)"></i>Bereits zuvor im Zeitraum gesehen</span></div>
	<div aria-hidden="true"><Chart data={points} x="index" y="fresh" xScale={scaleLinear()} yDomain={[0, maximum]} height={140} padding={8} axis={false} grid={false}>
		{#snippet marks()}<Spline stroke="var(--color-brand)" strokeWidth={2} /><Spline y="returning" stroke="var(--chart-path-2)" strokeWidth={2} stroke-dasharray="5 4" />{/snippet}
	</Chart></div>
	<details><summary>Werte je Abschnitt und Sitzungen anzeigen</summary>
		<div class="table-scroll"><table class="table"><thead><tr><th>Ab</th><th>Neu im Zeitraum</th><th>Zuvor im Zeitraum</th></tr></thead><tbody>
			{#each points as point}<tr><td><a href={sessionLink({ ...range, start: point.bucketStart < range.start ? range.start : point.bucketStart, end: [addDays(point.bucketStart, range.granularity === "day" ? 0 : 6), range.end].sort()[0] })}>{date(point.bucketStart)}</a></td><td>{number.format(point.fresh)}</td><td>{number.format(point.returning)}</td></tr>{/each}
		</tbody></table></div>
	</details>
</section>
