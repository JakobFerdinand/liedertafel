<script lang="ts">
	import { createEventDispatcher } from "svelte";
	import { AnnotationRange, Axis, Chart, Circle, Points, Rule, Text } from "layerchart";

	type HistoryEvent = {
		id: string;
		year: number;
		label: string;
		title: string;
	};

	type EraOverview = {
		from: number;
		to: number | null;
		title: string;
		rangeLabel: string;
		message: string;
	};

	type Row = HistoryEvent & {
		row: number;
		eraIndex: number;
	};

	type Props = {
		events: HistoryEvent[];
		eras: EraOverview[];
		today: number;
		activeEra: number | null;
	};

	let { events, eras, today, activeEra = null }: Props = $props();

	const dispatch = createEventDispatcher<{
		dotenter: { id: string; clientX: number; clientY: number };
		dotleave: Record<string, never>;
		jump: { kind: "entry"; id: string } | { kind: "era"; from: number };
	}>();

	let chartWrapper = $state<HTMLElement | null>(null);
	let hoveredId = $state<string | null>(null);
	let tooltip = $state<{ x: number; y: number } | null>(null);

	const rowsByEra: Map<number, HistoryEvent[]> | null = $derived.by(() => {
		if (eras.length === 0) {
			return null;
		}

		const map = new Map<number, HistoryEvent[]>();
		for (const event of events) {
			const index = eras.findLastIndex((era) => event.year >= era.from);
			const eraIndex = index >= 0 ? index : 0;
			if (!map.has(eraIndex)) {
				map.set(eraIndex, []);
			}
			map.get(eraIndex)?.push(event);
		}
		return map;
	});

	const rows: Row[] = $derived.by(() => {
		if (!rowsByEra) {
			return [];
		}

		const result: Row[] = [];
		for (const [eraIndex, eraEvents] of rowsByEra) {
			const sorted = [...eraEvents].sort((first, second) => first.year - second.year);
			sorted.forEach((event, row) => {
				result.push({ ...event, row, eraIndex });
			});
		}
		return result;
	});

	const minYear = $derived(events.reduce((min, event) => Math.min(min, event.year), today));
	const maxRow = $derived(rows.reduce((max, row) => Math.max(max, row.row), 0));
	const chartRows = $derived([...rows].sort((first, second) => first.year - second.year));
	const hoveredEvent = $derived(hoveredId ? rows.find((row) => row.id === hoveredId) ?? null : null);

	const eraBounds = $derived(
		eras.map((era, index) => ({
			from: era.from,
			to: era.to ?? today,
			top: 0.75,
			bottom: (rowsByEra?.get(index)?.length ?? 0) - 1 - 0.75,
		})),
	);

	function jumpToEntry(id: string) {
		dispatch("jump", { kind: "entry", id });
	}

	function onPointEnter(event: MouseEvent, id: string) {
		if (!chartWrapper) {
			return;
		}

		const rect = chartWrapper.getBoundingClientRect();
		const width = chartWrapper.clientWidth;
		const x = Math.min(Math.max(event.clientX - rect.left, 90), width - 90);
		tooltip = { x, y: event.clientY - rect.top };
		hoveredId = id;
	}

	function onPointLeave() {
		hoveredId = null;
		tooltip = null;
	}

	function onPointKeydown(event: KeyboardEvent, id: string) {
		if (event.key === "Enter" || event.key === " ") {
			event.preventDefault();
			jumpToEntry(id);
		}
	}
</script>

<div class="chart" bind:this={chartWrapper}>
	{#if rows.length > 0 && eras.length > 0}
		<Chart
			data={chartRows}
			x={(row: Row) => row.year}
			y={(row: Row) => row.row}
			xDomain={[minYear - 6, today + 7]}
			yDomain={[-1.1, maxRow + 1.1]}
			height={240}
			padding={{ top: 28, right: 16, bottom: 26, left: 12 }}
			grid={{ stroke: "rgba(255, 255, 255, 0.07)" }}
			axis={chartAxis}
			marks={chartMarks}
		/>
	{/if}
	{#if tooltip && hoveredEvent}
		<div class="tooltip" style={`left: ${tooltip.x}px; top: ${tooltip.y}px`} role="tooltip">
			<span class="tooltip__date">{hoveredEvent.label}</span>
			<span class="tooltip__title">{hoveredEvent.title}</span>
		</div>
	{/if}
</div>

{#snippet chartAxis()}
	<Axis placement="bottom" tickMarks={false} stroke="rgba(255, 255, 255, 0.3)" fill="rgba(255, 255, 255, 0.78)" />
{/snippet}

{#snippet pointLayer({ points })}
	{#each points as point (point.data.id)}
		<Circle
			cx={point.x}
			cy={point.y}
			r={point.data.id === hoveredId ? 9 : 6}
			fill={point.data.id === hoveredId ? "#ffffff" : "rgba(255, 255, 255, 0.92)"}
			stroke="rgba(255, 255, 255, 0.85)"
			stroke-width={point.data.id === hoveredId ? 2 : 1}
			opacity={activeEra === null || point.data.eraIndex === activeEra ? 1 : 0.3}
			class={point.data.id === hoveredId ? "chart-dot chart-dot--active" : "chart-dot"}
			tabindex="0"
			role="button"
			aria-label={`${point.data.label} – ${point.data.title}`}
			onclick={() => jumpToEntry(point.data.id)}
			onkeydown={(event) => onPointKeydown(event, point.data.id)}
			onmouseenter={(event) => onPointEnter(event, point.data.id)}
			onmouseleave={onPointLeave}
		/>
	{/each}
{/snippet}

{#snippet chartMarks()}
	{#each eraBounds as bounds, index (bounds.from)}
		<AnnotationRange
			x={[bounds.from, bounds.to]}
			y={[bounds.bottom, bounds.top]}
			class={activeEra === index ? "era-band era-band--active" : "era-band"}
			fill={activeEra === index ? "rgba(255, 255, 255, 0.16)" : "rgba(255, 255, 255, 0.07)"}
			props={{ rect: { onclick: () => dispatch("jump", { kind: "era", from: eras[index]?.from ?? bounds.from }) } }}
		/>
	{/each}
	<Rule x={today} axis="x" class="today-rule" stroke="rgba(255, 255, 255, 0.45)" />
	<Text x={today} y={maxRow} dy={-12} value="heute" class="today-label" />
	<Points r={6} children={pointLayer} />
{/snippet}

<style>
	.chart {
		position: relative;
	}

	.chart :global(.today-rule) {
		stroke-dasharray: 4 4;
	}

	.chart :global(.today-label) {
		font-size: 10px;
		font-weight: 600;
		letter-spacing: 0.1em;
		text-transform: uppercase;
		fill: rgba(255, 255, 255, 0.7);
	}

	.chart :global(.era-band) {
		cursor: pointer;
		transition: fill 0.2s ease;
	}

	.chart :global(.chart-dot) {
		cursor: pointer;
		transition: r 0.15s ease, opacity 0.2s ease;
	}

	.tooltip {
		position: absolute;
		z-index: 2;
		transform: translate(-50%, calc(-100% - 10px));
		pointer-events: none;
		display: flex;
		flex-direction: column;
		gap: 0.1rem;
		max-width: 16rem;
		padding: 0.55rem 0.75rem;
		border: 1px solid rgba(255, 255, 255, 0.4);
		background: var(--color-brand);
		box-shadow: 0 8px 20px rgba(0, 0, 0, 0.28);
	}

	.tooltip__date {
		font-size: 0.72rem;
		font-weight: 600;
		letter-spacing: 0.08em;
		text-transform: uppercase;
		opacity: 0.8;
	}

	.tooltip__title {
		font-size: 0.92rem;
		line-height: 1.35;
	}

	@media (max-width: 600px) {
		.chart {
			margin: 0 -0.5rem;
		}
	}
</style>