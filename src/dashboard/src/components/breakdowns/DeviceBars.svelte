<script lang="ts">
	import { sessionLink, type RangeState } from "../../lib/date-range";
	import { number } from "../../lib/format";
	let { devices, range }: { devices: { device: string; count: number }[]; range: RangeState } = $props();
	const maximum = $derived(Math.max(1, ...devices.map((d) => d.count)));
</script>
<section class="card">
	<h2>Geräteklassen</h2>
	<p class="chart-caption">Aus der gemeldeten Bildschirmbreite, nicht der Fensterbreite. Fehlende Werte sind unbekannt.</p>
	{#each devices as device}
		<a class="device" href={sessionLink(range, { device: device.device })}>
			<span>{device.device}</span><span class="track"><span style:width={`${device.count / maximum * 100}%`}></span></span><span>{number.format(device.count)}</span>
		</a>
	{/each}
</section>
<style>
	.device { display: grid; grid-template-columns: 6rem 1fr 3rem; gap: 0.75rem; align-items: center; margin-top: 0.8rem; font-size: 0.9rem; }
	.device > span:last-child { text-align: right; }
	.track { height: 0.75rem; background: var(--color-accent-soft); border-radius: 3px; overflow: hidden; }
	.track span { height: 100%; background: var(--color-brand); display: block; }
</style>
