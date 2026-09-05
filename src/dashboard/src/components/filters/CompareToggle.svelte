<script lang="ts">
	let { checked, onchange }: { checked: boolean; onchange: (checked: boolean) => void } = $props();
</script>
<label class="compare"><input type="checkbox" {checked} onchange={(event) => onchange(event.currentTarget.checked)} /> Vorperiode vergleichen</label>
<style>
	.compare { display: flex; gap: 0.5rem; align-items: center; margin-top: 1rem; }
</style>
