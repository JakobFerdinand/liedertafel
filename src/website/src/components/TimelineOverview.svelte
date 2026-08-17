<script lang="ts">
	import { createEventDispatcher, onMount } from "svelte";
	import type { Component } from "svelte";
	import type { HistoryEvent, EraOverview } from "./TimelineChart.svelte";

	type Props = {
		events: HistoryEvent[];
		eras: EraOverview[];
		today: number;
	};

	let { events, eras, today }: Props = $props();

	const dispatch = createEventDispatcher<{
		jump: { kind: "entry"; id: string } | { kind: "era"; from: number };
	}>();

	let chartCtor = $state<Component | null>(null);
	let activeEra = $state<number | null>(null);

	onMount(async () => {
		const module = await import("./TimelineChart.svelte");
		chartCtor = module.default;
	});

	function jumpToEntry(id: string) {
		dispatch("jump", { kind: "entry", id });
	}

	function jumpToEra(from: number) {
		activeEra = null;
		dispatch("jump", { kind: "era", from });
	}

	function handleChartJump(event: { detail: { kind: string; id?: string; from?: number } }) {
		const detail = event.detail;

		if (detail.kind === "entry" && detail.id) {
			jumpToEntry(detail.id);
			return;
		}

		if (detail.kind === "era" && detail.from) {
			jumpToEra(detail.from);
		}
	}

	const eraCounts = new Map<number, number>();
	for (const event of events) {
		const index = eras.findLastIndex((era) => event.year >= era.from);
		const eraIndex = index >= 0 ? index : 0;
		eraCounts.set(eraIndex, (eraCounts.get(eraIndex) ?? 0) + 1);
	}
</script>

<div class="overview">
	<div class="overview__header">
		<p class="overview__eyebrow">1897 – heute</p>
		<p class="overview__hint">Punkte und Epochen anklicken, um direkt zur Chronik zu springen.</p>
	</div>
	<div class="overview__chart">
		{#if chartCtor}
			<svelte:component this={chartCtor} {events} {eras} {today} {activeEra} on:jump={handleChartJump} />
		{:else}
			<ul class="overview__fallback" aria-label="Epochen im Überblick">
				{#each eras as era, index (era.from)}
					<li>
						<a href={`#era-${era.from}`}>
							<span class="overview__fallback-title">{era.title}</span>
							<span class="overview__fallback-range">{era.rangeLabel}</span>
							<span class="overview__fallback-count">{eraCounts.get(index) ?? 0}</span>
						</a>
					</li>
				{/each}
			</ul>
		{/if}
	</div>
	{#if eras.length > 0}
		<ol class="overview__legend" aria-label="Epochen der Vereinsgeschichte">
			{#each eras as era, index (era.from)}
				<li>
					<a
						class={activeEra === index ? "overview__legend-link overview__legend-link--active" : "overview__legend-link"}
						href={`#era-${era.from}`}
						onmouseenter={() => {
							activeEra = index;
						}}
						onmouseleave={() => {
							activeEra = null;
						}}
						onclick={(event) => {
							event.preventDefault();
							jumpToEra(era.from);
						}}
					>
						<span class="overview__legend-chip" aria-hidden="true"></span>
						<span class="overview__legend-title">{era.title}</span>
						<span class="overview__legend-range">{era.rangeLabel}</span>
					</a>
				</li>
			{/each}
		</ol>
	{/if}
</div>

<style>
	.overview {
		display: flex;
		flex-direction: column;
		gap: 1rem;
	}

	.overview__header {
		display: flex;
		align-items: baseline;
		justify-content: space-between;
		gap: 1rem;
	}

	.overview__eyebrow {
		margin: 0;
		font-size: 0.8rem;
		font-weight: 600;
		letter-spacing: 0.14em;
		text-transform: uppercase;
		opacity: 0.75;
		white-space: nowrap;
	}

	.overview__hint {
		margin: 0;
		font-size: 0.9rem;
		line-height: 1.5;
		opacity: 0.85;
		text-align: right;
	}

	.overview__chart {
		border: 1px solid rgba(255, 255, 255, 0.22);
		background: rgba(255, 255, 255, 0.05);
		padding: 1rem 1rem 0.5rem;
	}

	.overview__fallback {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-wrap: wrap;
		gap: 0.5rem;
	}

	.overview__fallback a {
		display: inline-flex;
		align-items: center;
		gap: 0.55rem;
		padding: 0.5rem 0.9rem;
		border: 1px solid rgba(255, 255, 255, 0.28);
		background: rgba(255, 255, 255, 0.08);
		color: #ffffff;
		text-decoration: none;
	}

	.overview__fallback a:hover,
	.overview__fallback a:focus-visible {
		background: rgba(255, 255, 255, 0.18);
	}

	.overview__fallback-title {
		font-family: var(--font-heading);
		font-size: 1rem;
	}

	.overview__fallback-range {
		font-size: 0.72rem;
		font-weight: 600;
		letter-spacing: 0.08em;
		text-transform: uppercase;
		opacity: 0.7;
	}

	.overview__fallback-count {
		min-width: 1.5rem;
		height: 1.5rem;
		padding: 0 0.35rem;
		border-radius: 999px;
		background: rgba(255, 255, 255, 0.18);
		font-size: 0.8rem;
		font-weight: 600;
		line-height: 1.5rem;
		text-align: center;
	}

	.overview__legend {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-wrap: wrap;
		gap: 0.5rem;
	}

	.overview__legend a {
		display: inline-flex;
		align-items: center;
		gap: 0.55rem;
		padding: 0.5rem 0.9rem;
		border: 1px solid rgba(255, 255, 255, 0.28);
		background: rgba(255, 255, 255, 0.08);
		color: #ffffff;
		text-decoration: none;
		transition: background 0.2s ease, border-color 0.2s ease;
	}

	.overview__legend a:hover,
	.overview__legend a:focus-visible,
	.overview__legend a:global(.overview__legend-link--active) {
		background: rgba(255, 255, 255, 0.18);
		border-color: rgba(255, 255, 255, 0.65);
	}

	.overview__legend-chip {
		width: 0.7rem;
		height: 0.7rem;
		border-radius: 2px;
		background: rgba(255, 255, 255, 0.9);
	}

	.overview__legend-title {
		font-family: var(--font-heading);
		font-size: 1rem;
		line-height: 1.2;
	}

	.overview__legend-range {
		font-size: 0.72rem;
		font-weight: 600;
		letter-spacing: 0.08em;
		text-transform: uppercase;
		opacity: 0.7;
	}

	@media (max-width: 600px) {
		.overview__header {
			flex-direction: column;
			gap: 0.35rem;
		}

		.overview__hint {
			text-align: left;
		}

		.overview__chart {
			padding: 0.5rem 0.25rem 0.25rem;
		}
	}
</style>