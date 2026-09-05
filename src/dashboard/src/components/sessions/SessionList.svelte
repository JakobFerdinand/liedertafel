<script lang="ts">
	import type { SessionResponse } from "../../lib/api-client";
	import { number, timestamp } from "../../lib/format";
	let { data, params, loading, onnext }: { data: SessionResponse; params: URLSearchParams; loading: boolean; onnext: () => void } = $props();
	function detailLink(ref: string) { const query = new URLSearchParams(params); query.set("session", ref); return `/sessions?${query}`; }
</script>
<section class="section" aria-labelledby="sessions-list-heading">
	<h2 id="sessions-list-heading">Beobachtete Sitzungen</h2>
	<p>{number.format(data.totalSessions)} passende Sitzungs-Kennungen. {number.format(data.withoutSessionId)} Aufrufe ohne Sitzungs-ID sind ausgeschlossen.</p>
	{#if data.truncated}<p class="notice">Die Suche ist unvollständig. Bitte den Zeitraum verkleinern; auch die Sitzungszahlen können unvollständig sein.</p>{/if}
	{#if data.items.length === 0}
		<p class="empty">Keine Sitzungen mit Sitzungs-ID für diesen Zeitraum und diese Filter gefunden. Verändere den Zeitraum oder setze die Filter zurück.</p>
	{:else}
		<div class="card table-scroll"><table class="table">
			<caption class="sr-only">Sitzungen nach letztem beobachteten Aufruf, neueste zuerst</caption>
			<thead><tr><th>Sitzung</th><th>Erstes Ereignis</th><th>Letztes Ereignis</th><th>Aufrufe</th><th>Eindeutige Seiten</th><th>Einstiegsseite</th><th>Letzte Seite</th><th>Reloads</th><th>Gerät</th></tr></thead>
			<tbody>{#each data.items as row}<tr>
				<td><a href={detailLink(row.sessionRef)}>Details anzeigen <span>{row.sessionId}</span></a><small>Besucher: {row.visitorId}</small></td>
				<td>{timestamp(row.firstSeen)}</td><td>{timestamp(row.lastSeen)}</td><td>{number.format(row.viewCount)}</td><td>{number.format(row.distinctPathCount)}</td>
				<td>{row.entryPath}</td><td>{row.lastPath}</td><td>{number.format(row.reloadCount)}</td><td>{row.deviceCategory}</td>
			</tr>{/each}</tbody>
		</table></div>
	{/if}
	<p class="chart-caption">Erster und letzter Aufruf innerhalb des Suchzeitraums; Gerät aus der ersten beobachteten Bildschirmbreite. Stand: {timestamp(data.generatedAt)}. Folgeseiten verwenden diesen Stand für bis zu fünf Minuten.</p>
	{#if data.nextCursor}<button type="button" disabled={loading} onclick={onnext}>Weitere Sitzungen anzeigen</button>{/if}
</section>
<style>
	.table { min-width: 1000px; }
	td { vertical-align: top; max-width: 15rem; overflow-wrap: anywhere; }
	small, td a span { display: block; }
	small { color: var(--color-text-muted); margin-top: 0.3rem; }
</style>
