<script lang="ts">
	import type { SessionDetail } from "../../lib/api-client";
	import { date, gap, timestamp } from "../../lib/format";
	let { data }: { data: SessionDetail } = $props();
	const badges: Record<string, string> = { navigate: "Seitenaufruf", reload: "Reload", back_forward: "Zurück / Vorwärts", unknown: "unbekannt" };
</script>
<section class="card section" aria-labelledby="timeline-heading">
	<h2 id="timeline-heading">Seitenaufrufe der Sitzung {data.sessionId}</h2>
	<p>Besucher-Kennung: {data.visitorId}. Beobachtetes Fenster: {date(data.range.start)} – {date(data.range.end)}.</p>
	<p class="chart-caption">Ungefähre Beobachtungszeit: letzter Speicherzeitpunkt in Table Storage, kein Klickzeitpunkt. Bei gleichen Zeitpunkten ist die tatsächliche Reihenfolge unbekannt.</p>
	{#if data.truncated}<p class="notice" role="status">Die Abfrage ist unvollständig. Es können Aufrufe und damit Zwischenschritte fehlen. Bitte den Zeitraum verkleinern.</p>{/if}
	{#if data.possiblyTruncatedStart}<p class="notice">Die Sitzung beginnt möglicherweise vor dem Suchzeitraum. Frühere Aufrufe sind nicht enthalten.</p>{/if}
	{#if data.possiblyTruncatedEnd}<p class="notice">Die Sitzung setzt sich möglicherweise nach dem Suchzeitraum fort. Spätere Aufrufe sind nicht enthalten.</p>{/if}
	<ol class="timeline">
		{#each data.events as event, index}
			<li>
				{#if event.gapSeconds !== null}<p class="gap">Zeit seit letztem Aufruf: {gap(event.gapSeconds)}</p>{/if}
				<div class="event-heading"><time datetime={event.observedAt ?? undefined}>{timestamp(event.observedAt)}</time><span class="badge">{badges[event.navigationType] ?? "unbekannt"}</span></div>
				<p class="path">{event.path}</p>
				<p class="chart-caption">Herkunft: {event.referrerHost || "unbekannt / nicht übermittelt"}<br />Gerät: {event.deviceCategory}{event.viewportWidth > 0 ? ` (${event.viewportWidth} px Bildschirmbreite)` : ""}</p>
				{#if index > 0 && event.observedAt === data.events[index - 1].observedAt}<p class="chart-caption">Gleicher Speicherzeitpunkt wie der vorherige Aufruf; Reihenfolge nicht eindeutig.</p>{/if}
			</li>
		{/each}
	</ol>
</section>
<style>
	.timeline { list-style: none; margin: 1.5rem 0 0 0.5rem; padding: 0; border-left: 2px solid var(--color-border); }
	li { position: relative; padding: 0 0 1.75rem 1.5rem; }
	li::before { content: ""; position: absolute; left: -6px; top: 0.4rem; width: 10px; height: 10px; border-radius: 50%; background: var(--color-brand); }
	.event-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 0.6rem; }
	time { font-variant-numeric: tabular-nums; font-size: 0.9rem; }
	.path { font-size: 1.15rem; font-weight: 600; overflow-wrap: anywhere; margin: 0.5rem 0; }
	.badge { font-size: 0.8rem; background: var(--color-accent-soft); border-radius: 4px; padding: 0.15rem 0.45rem; }
	.gap { color: var(--color-text-muted); font-size: 0.85rem; margin: 0 0 0.5rem; }
</style>
