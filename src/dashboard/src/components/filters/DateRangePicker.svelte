<script lang="ts">
	import { addDays, addMonths, dailyMaximum, span, today, type RangeState } from "../../lib/date-range";
	let { value, onchange, sessions = false }: { value: RangeState; onchange: (value: RangeState) => void; sessions?: boolean } = $props();
	let start = $state("");
	let end = $state("");
	let granularity = $state("day");
	let preset = $state("custom");
	let forcedWeek = $state(false);
	const presets = [{ key: "7", label: "7 Tage" }, { key: "14", label: "14 Tage" }, { key: "28", label: "28 Tage" }, { key: "3m", label: "3 Monate" }, { key: "6m", label: "6 Monate" }];
	function presetStart(key: string, last: string) { return key.endsWith("m") ? addDays(addMonths(last, -Number(key[0])), 1) : addDays(last, 1 - Number(key)); }
	$effect(() => {
		start = value.start; end = value.end; granularity = value.granularity;
		preset = value.end === today() ? presets.find((p) => presetStart(p.key, value.end) === value.start)?.key ?? "custom" : "custom";
	});
	const longRange = $derived(span({ start, end }) > dailyMaximum);
	function apply() {
		forcedWeek = !sessions && longRange && granularity === "day";
		if (forcedWeek) granularity = "week";
		onchange({ ...value, start, end, granularity });
	}
	function choose(key: string) {
		preset = key;
		if (key !== "custom") { end = today(); start = presetStart(key, end); if (!sessions) granularity = span({ start, end }) > dailyMaximum ? "week" : "day"; apply(); }
	}
</script>

<form class="range-controls" onsubmit={(event) => { event.preventDefault(); apply(); }}>
	<label>Zeitraum
		<select value={preset} onchange={(event) => choose(event.currentTarget.value)}>
			{#each presets.filter((p) => !sessions || p.key !== "6m") as option}
				<option value={option.key}>{option.label}</option>
			{/each}
			<option value="custom">Benutzerdefiniert</option>
		</select>
	</label>
	{#if preset === "custom"}
		<label>Von <input type="date" bind:value={start} required /></label>
		<label>Bis einschließlich <input type="date" bind:value={end} required /></label>
	{/if}
	{#if !sessions}
		<label>Auflösung
			<select bind:value={granularity}>
				<option value="day" disabled={longRange}>Tag</option>
				<option value="week">Woche</option>
			</select>
		</label>
	{/if}
	<button type="submit">Zeitraum anwenden</button>
</form>
{#if !sessions && (longRange || forcedWeek)}
	<p class="chart-caption">Über 92 Tage wird die Auflösung Woche verwendet. Für einzelne Tage bitte den Zeitraum verkürzen.</p>
{/if}
<p class="chart-caption">Alle Datumsgrenzen und Uhrzeiten: Europe/Vienna. Das Enddatum ist eingeschlossen.</p>

<style>
	.range-controls { display: flex; align-items: end; flex-wrap: wrap; gap: 0.75rem; }
</style>
