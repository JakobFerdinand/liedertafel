<script lang="ts">
	let { value, onchange }: { value: URLSearchParams; onchange: (filters: URLSearchParams) => void } = $props();
	let path = $state("");
	let originHost = $state("");
	let device = $state("");
	let hasReload = $state("");
	let minViews = $state<number | undefined>(1);
	$effect(() => { path = value.get("path") ?? ""; originHost = value.get("originHost") ?? ""; device = value.get("device") ?? ""; hasReload = value.get("hasReload") ?? ""; minViews = Number(value.get("minViews") ?? "1"); });
	function apply() {
		const filters = new URLSearchParams();
		for (const [key, value] of Object.entries({ path, originHost, device, hasReload, minViews })) if (value) filters.set(key, String(value));
		onchange(filters);
	}
</script>
<form class="filters" onsubmit={(event) => { event.preventDefault(); apply(); }}>
	<label>Seitenpfad <input type="text" bind:value={path} placeholder="z. B. /kontakt" maxlength="2048" /></label>
	<label>Externe Herkunft <input type="text" bind:value={originHost} placeholder="z. B. google.com" maxlength="253" /></label>
	<label>Geräteklasse <select bind:value={device}><option value="">Alle</option>{#each ["Unbekannt", "Mobil", "Tablet", "Laptop", "Breitbild"] as item}<option>{item}</option>{/each}</select></label>
	<label>Reloads in der Sitzung <select bind:value={hasReload}><option value="">Alle</option><option value="true">Mit Reload</option><option value="false">Ohne Reload</option></select></label>
	<label>Mindestens Aufrufe <input type="number" min="1" max="200000" step="1" bind:value={minViews} /></label>
	<button type="submit">Sitzungen suchen</button>
	<button type="button" onclick={() => onchange(new URLSearchParams())}>Filter zurücksetzen</button>
</form>
<p class="chart-caption">Seite, Herkunft und Geräteklasse müssen auf einen gemeinsamen Aufruf passen. Aufrufzahlen und Reloads beziehen sich auf die gesamte beobachtete Sitzung im Zeitraum.</p>
<style>
	.filters { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 0.75rem; align-items: end; margin-top: 1.5rem; }
	input { width: 100%; }
</style>
