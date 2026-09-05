<script lang="ts">
	import { Chart, Spline } from "layerchart";
	import { scaleLinear } from "d3-scale";
	import { delta, number } from "../../lib/format";
	let { label, value, previous, values, description, primary = false }: { label: string; value: number; previous?: number; values: number[]; description: string; primary?: boolean } = $props();
	const points = $derived(values.map((count, index) => ({ index, count })));
</script>
<article class="card" class:kpi-main={primary}>
	<h2 class="label">{label}</h2>
	<p class="kpi-value">{number.format(value)}</p>
	{#if previous !== undefined}<p class="delta">{delta(value, previous)} <span>zur Vorperiode</span></p>{/if}
	<div aria-hidden="true">
		<Chart data={points} x="index" y="count" xScale={scaleLinear()} yDomain={[0, Math.max(1, ...values)]} height={44} padding={3} axis={false}>
			{#snippet marks()}<Spline stroke={primary ? "var(--color-surface)" : "var(--color-brand)"} strokeWidth={2} />{/snippet}
		</Chart>
	</div>
	<p class="description">{description}</p>
</article>
<style>
	.label { font-size: 0.95rem; margin: 0 0 0.5rem; }
	.delta { font-size: 0.85rem; margin: 0.5rem 0; }
	.delta span { display: block; }
	.description { font-size: 0.8rem; margin: 0.5rem 0 0; }
</style>
