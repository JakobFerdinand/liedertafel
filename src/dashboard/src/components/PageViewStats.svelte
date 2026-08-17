<script lang="ts">
	import { onMount } from "svelte";
	import { Axis, Bars, Chart, Highlight, Text, Tooltip } from "layerchart";
	import type { TextProps } from "layerchart";

	type StatsResponse = {
		total: number;
		uniquePaths: number;
		topPaths: { path: string; count: number }[];
		series: { week: string; count: number }[];
		pathSeries: { week: string; path: string; count: number }[];
		devices: { device: string; count: number }[];
		deviceSeries: { week: string; device: string; count: number }[];
		origins: { origin: string; count: number }[];
		originSeries: { week: string; origin: string; count: number }[];
	};

	type Days = 28 | 90 | 180;

	type SeriesRow = { week: string; count: number };
	type KeyedRow = { week: string; count: number; path?: string; device?: string; origin?: string };
	type SeriesDef = {
		key: string;
		label: string;
		data: SeriesRow[];
		value: (d: SeriesRow) => number;
		color: string;
	};
	type ChartSnippetArgs = {
		context: {
			series: {
				visibleSeries: { key: string; label?: string; data?: unknown[]; color?: string }[];
				isHighlighted: (key: string, defaultValue?: boolean) => boolean;
			};
			tooltip: { series: { key: string; label?: string; value?: number; color?: string }[] };
		};
	};

	const nfInt = new Intl.NumberFormat("de-AT");
	const nfCompact = new Intl.NumberFormat("de-AT", { maximumFractionDigits: 0 });
	const dfLong = new Intl.DateTimeFormat("de-AT", {
		day: "2-digit",
		month: "2-digit",
		year: "numeric",
	});

	function fmtWeekShort(week: string): string {
		const [, month, day] = week.split("-");
		return `${day}.${month}.`;
	}

	function fmtWeekLong(week: string): string {
		const [year, month, day] = week.split("-").map(Number);
		return dfLong.format(new Date(year, month - 1, day));
	}

	function fmtTick(value: string | number | ((d: any) => string | number) | undefined): string {
		if (typeof value !== "string" && typeof value !== "number") {
			return "";
		}
		if (typeof value === "number") {
			return nfCompact.format(value);
		}
		const plain = value.replace(/,/g, "");
		const num = Number(plain);
		return /^-?\d+(\.\d+)?$/.test(plain) && Number.isFinite(num) ? nfCompact.format(num) : value;
	}

	function cssVar(name: string, fallback: string): string {
		return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
	}

	const chartPalettes = $derived.by(() => {
		const pathFallbacks = ["#823c41", "#b56b6f", "#d9a0a3", "#8a5a6b", "#c58b7f", "#a3794f"];
		const path = [
			"--chart-path-1",
			"--chart-path-2",
			"--chart-path-3",
			"--chart-path-4",
			"--chart-path-5",
			"--chart-path-6",
			"--chart-other",
		].map((v, i) => cssVar(v, pathFallbacks[i] ?? "#b9b3ab"));
		const deviceFallbacks = ["#823c41", "#b56b6f", "#d9a0a3", "#6d7075"];
		const device = ["--chart-device-1", "--chart-device-2", "--chart-device-3", "--chart-device-4"].map(
			(v, i) => cssVar(v, deviceFallbacks[i] ?? "#6d7075"),
		);
		const originFallbacks = ["#823c41", "#b56b6f", "#d9a0a3", "#8a5a6b", "#c58b7f", "#a3794f"];
		const origin = [
			"--chart-origin-1",
			"--chart-origin-2",
			"--chart-origin-3",
			"--chart-origin-4",
			"--chart-origin-5",
			"--chart-origin-6",
		].map((v, i) => cssVar(v, originFallbacks[i] ?? "#823c41"));
		return { path, device, origin };
	});

	function makeSeries(
		names: { name: string }[],
		rows: KeyedRow[],
		keyOf: (r: KeyedRow) => string | undefined,
		colors: string[],
	): SeriesDef[] {
		return names.map((n, i) => {
			const color = colors[i] ?? colors[colors.length - 1] ?? "#823c41";
			return {
				key: n.name,
				label: n.name,
				data: rows.filter((r) => keyOf(r) === n.name).map((r) => ({ week: r.week, count: r.count })),
				value: (d: SeriesRow) => d.count,
				color,
			};
		});
	}

	let days = $state<Days>(28);
	let loading = $state(true);
	let error = $state(false);
	let data = $state<StatsResponse | null>(null);
	let controller: AbortController | null = null;

	async function load() {
		controller?.abort();
		const ac = new AbortController();
		controller = ac;
		loading = true;
		error = false;
		try {
			const res = await fetch(`/api/pageviews/stats?days=${days}`, { signal: ac.signal });
			if (!res.ok) {
				throw new Error(`HTTP ${res.status}`);
			}
			const json = (await res.json()) as StatsResponse;
			if (ac.signal.aborted) {
				return;
			}
			data = json;
		} catch (err) {
			if (ac.signal.aborted) {
				return;
			}
			error = true;
		} finally {
			if (!ac.signal.aborted) {
				loading = false;
			}
		}
	}

	onMount(() => {
		void load();
	});

	const segments = $derived([
		{ days: 28 as Days, label: "4 Wochen" },
		{ days: 90 as Days, label: "3 Monate" },
		{ days: 180 as Days, label: "6 Monate" },
	]);

	const weeks = $derived(data?.series.map((r) => r.week) ?? []);

	const topPath = $derived(data?.topPaths[0]?.path ?? "–");
	const topPathCount = $derived(data?.topPaths[0]?.count ?? 0);

	function seriesNames(rows: KeyedRow[], keyOf: (r: KeyedRow) => string | undefined): string[] {
		const names: string[] = [];
		for (const row of rows) {
			const name = keyOf(row);
			if (name !== undefined && !names.includes(name)) {
				names.push(name);
			}
		}
		return names;
	}

	const pathNames = $derived.by(() => seriesNames(data?.pathSeries ?? [], (r) => r.path));

	const pathDefs = $derived.by(() =>
		data
			? makeSeries(
					pathNames.map((name) => ({ name })),
					data.pathSeries,
					(r) => r.path,
					chartPalettes.path,
				)
			: [],
	);

	const deviceDefs = $derived.by(() =>
		data
			? makeSeries(
					data.devices.map((d) => ({ name: d.device })),
					data.deviceSeries,
					(r) => r.device,
					chartPalettes.device,
				)
			: [],
	);

	const originDefs = $derived.by(() =>
		data
			? makeSeries(
					data.origins.map((o) => ({ name: o.origin })),
					data.originSeries,
					(r) => r.origin,
					chartPalettes.origin,
				)
			: [],
	);

	const maxDevice = $derived(Math.max(1, ...(data?.devices.map((d) => d.count) ?? [0])));

	function cellCount(rows: KeyedRow[], week: string, name: string, keyOf: (r: KeyedRow) => string | undefined): number {
		return rows.find((r) => r.week === week && keyOf(r) === name)?.count ?? 0;
	}
</script>

{#snippet weekTickLabel({ props: labelProps }: { props: TextProps; index: number })}
		<Text {...labelProps} value={fmtWeekShort(String(labelProps.value ?? ""))} />
	{/snippet}

	{#snippet countTickLabel({ props: labelProps }: { props: TextProps; index: number })}
		<Text {...labelProps} value={fmtTick(labelProps.value)} />
	{/snippet}

	{#snippet chartAxis(args: ChartSnippetArgs)}
		<Axis
			placement="bottom"
			tickMarks={false}
			fill="#6b6862"
			stroke="#d6d2cb"
			tickLabel={weekTickLabel}
		/>
		<Axis
			placement="left"
			tickMarks={false}
			fill="#6b6862"
			stroke="#d6d2cb"
			tickLabel={countTickLabel}
		/>
	{/snippet}

	{#snippet chartMarks(args: ChartSnippetArgs)}
		{#each args.context.series.visibleSeries as s, i (s.key)}
			<Bars
				seriesKey={s.key}
				data={s.data}
				rounded={i !== args.context.series.visibleSeries.length - 1 ? "none" : "edge"}
				radius={3}
				opacity={args.context.series.isHighlighted(s.key, true) ? 1 : 0.25}
			/>
		{/each}
	{/snippet}

	{#snippet chartTooltip(args: ChartSnippetArgs)}
		<Tooltip.Root>
			{#snippet children({ data: hoverData }: { data: any })}
				<Tooltip.Header value={fmtWeekLong(String(hoverData?.week ?? ""))} />
				<Tooltip.List>
					{#each args.context.tooltip.series as s (s.key)}
						{#if s.value != null}
							<Tooltip.Item label={s.label} value={nfInt.format(s.value)} color={s.color} valueAlign="right" />
						{/if}
					{/each}
				</Tooltip.List>
			{/snippet}
		</Tooltip.Root>
	{/snippet}

	{#snippet weekChart(args: { defs: SeriesDef[]; caption: string; srRows: KeyedRow[]; keyOf: (r: KeyedRow) => string | undefined })}
		<div class="card">
			<div class="legend">
				{#each args.defs as d (d.key)}
					<span><i aria-hidden="true" style={`background: ${d.color}`}></i>{d.label}</span>
				{/each}
			</div>
			<Chart
				data={data?.series ?? []}
				x={(d: SeriesRow) => d.week}
				y={(d: SeriesRow) => d.count}
				series={args.defs}
				seriesLayout="stack"
				valueAxis="y"
				bandPadding={0.4}
				height={260}
				padding={{ top: 12, right: 12, bottom: 32, left: 48 }}
				grid={{ stroke: "#e6e2db" }}
				axis={chartAxis}
				highlight={{ area: { fill: "rgba(130, 60, 65, 0.08)" } }}
				tooltipContext={{ mode: "band" }}
				marks={chartMarks}
				tooltip={chartTooltip}
			/>
			<div class="sr-only">
				<table>
					<caption>{args.caption}</caption>
					<thead>
						<tr>
							<th scope="col">Woche</th>
							{#each args.defs as d (d.key)}
								<th scope="col">{d.label}</th>
							{/each}
						</tr>
					</thead>
					<tbody>
						{#each weeks as week (week)}
							<tr>
								<th scope="row">{fmtWeekLong(week)}</th>
								{#each args.defs as d (d.key)}
									<td>{nfInt.format(cellCount(args.srRows, week, d.key, args.keyOf))}</td>
								{/each}
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		</div>
	{/snippet}

{#if loading}
	<div class="loading">Daten werden geladen …</div>
{:else if error}
	<div class="error">
		<p>Statistiken konnten nicht geladen werden.</p>
		<button class="retry-btn" type="button" onclick={() => void load()}>Erneut versuchen</button>
	</div>
{:else if data}
	{#if data.total === 0}
		<div class="empty">Noch keine Daten vorhanden.</div>
	{:else}
		<section class="section" aria-labelledby="heading-views">
			<h2 id="heading-views">Seitenaufrufe</h2>
			<div class="toggle-group" role="group" aria-label="Zeitraum">
				{#each segments as seg (seg.days)}
					<button
						type="button"
						class:active={days === seg.days}
						aria-pressed={days === seg.days}
						onclick={() => {
							days = seg.days;
							void load();
						}}
					>
						{seg.label}
					</button>
				{/each}
			</div>
			<div class="section-grid">
				<div class="card kpi kpi-main">
					<p class="kpi-label">Gesamt</p>
					<p class="kpi-value">{nfInt.format(data.total)}</p>
				</div>
				<div class="card kpi">
					<p class="kpi-label">Meistbesuchte Seite</p>
					<p class="kpi-value">
						{topPath}
						<small>{nfInt.format(topPathCount)} Aufrufe</small>
					</p>
				</div>
				<div class="card kpi">
					<p class="kpi-label">Einzigartige Seiten</p>
					<p class="kpi-value">{nfInt.format(data.uniquePaths)}</p>
				</div>
			</div>
		</section>

		<section class="section" aria-labelledby="heading-paths">
			<h2 id="heading-paths">Meistbesuchte Seiten</h2>
			<div class="card">
				<table class="table">
					<thead>
						<tr>
							<th scope="col">Seite</th>
							<th class="num" scope="col">Aufrufe</th>
						</tr>
					</thead>
					<tbody>
						{#each data.topPaths as p (p.path)}
							<tr>
								<td>{p.path}</td>
								<td class="num">{nfInt.format(p.count)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if pathDefs.length > 0}
				{@render weekChart({
					defs: pathDefs,
					caption: "Seitenaufrufe nach Woche und Seite",
					srRows: data.pathSeries,
					keyOf: (r) => r.path,
				})}
			{/if}
		</section>

		<section class="section" aria-labelledby="heading-devices">
			<h2 id="heading-devices">Geräte</h2>
			<div class="card">
				<div class="legend">
					{#each data.devices as d, i (d.device)}
						<span><i aria-hidden="true" style={`background: ${chartPalettes.device[i] ?? "#823c41"}`}></i>{d.device}</span>
					{/each}
				</div>
				{#each data.devices as d, i (d.device)}
					<div class="bar-row">
						<span class="bar-label">{d.device}</span>
						<div class="bar-track">
							<div
								class="bar-fill"
								style={`width: ${Math.round((d.count / maxDevice) * 100)}%; background: ${chartPalettes.device[i] ?? "#823c41"}`}
							></div>
						</div>
						<span class="bar-count">{nfInt.format(d.count)}</span>
					</div>
				{/each}
			</div>
			{#if deviceDefs.length > 0}
				{@render weekChart({
					defs: deviceDefs,
					caption: "Seitenaufrufe nach Woche und Gerät",
					srRows: data.deviceSeries,
					keyOf: (r) => r.device,
				})}
			{/if}
		</section>

		<section class="section" aria-labelledby="heading-origins">
			<h2 id="heading-origins">Herkunft</h2>
			<div class="card">
				<table class="table">
					<thead>
						<tr>
							<th scope="col">Herkunft</th>
							<th class="num" scope="col">Aufrufe</th>
						</tr>
					</thead>
					<tbody>
						{#each data.origins as o (o.origin)}
							<tr>
								<td>{o.origin}</td>
								<td class="num">{nfInt.format(o.count)}</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
			{#if originDefs.length > 0}
				{@render weekChart({
					defs: originDefs,
					caption: "Seitenaufrufe nach Woche und Herkunft",
					srRows: data.originSeries,
					keyOf: (r) => r.origin,
				})}
			{/if}
		</section>
	{/if}
{/if}