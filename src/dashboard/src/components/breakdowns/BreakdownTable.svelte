<script lang="ts">
	import { sessionLink, type RangeState } from "../../lib/date-range";
	import { number } from "../../lib/format";
	let { title, rows, filter, range, description }: { title: string; rows: { label: string; count: number }[]; filter: string; range: RangeState; description?: string } = $props();
	const maximum = $derived(Math.max(1, ...rows.map((r) => r.count)));
</script>
<section class="card breakdown">
	<h2>{title}</h2>
	{#if description}<p class="chart-caption">{description}</p>{/if}
	{#if rows.length === 0}<p>Keine Aufrufe für diese Auswertung im Zeitraum.</p>{:else}
		<table class="table"><thead><tr><th>{filter === "path" ? "Seite" : "Herkunft"}</th><th class="num">Aufrufe</th></tr></thead>
			<tbody>{#each rows as row}<tr><td><a href={sessionLink(range, { [filter]: row.label })} aria-label={`Sitzungen für ${row.label} anzeigen`}>{row.label}</a><span class="bar" style:width={`${row.count / maximum * 100}%`}></span></td><td class="num">{number.format(row.count)}</td></tr>{/each}</tbody>
		</table>
	{/if}
</section>
<style>
	.breakdown { min-width: 0; }
	td:first-child { overflow-wrap: anywhere; }
	.bar { display: block; height: 3px; background: var(--color-brand); margin-top: 0.3rem; }
</style>
