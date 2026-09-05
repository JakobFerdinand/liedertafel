using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DashboardApi.Shared.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace DashboardApi.Features.PageViews;

public sealed record SessionFilters(string? Path, string? OriginHost, string? Device, bool? HasReload, int MinViews)
{
	public static SessionFilters Parse(NameValueCollection query)
	{
		var device = query["device"];
		if (device != null && !InsightValues.Devices.Contains(device)) throw new QueryException("Unbekannte Geräteklasse.");
		bool? reload = null;
		if (query["hasReload"] is { } value) reload = bool.TryParse(value, out var parsed) ? parsed : throw new QueryException("Reload-Filter muss true oder false sein.");
		var min = Integer(query["minViews"], 1, 1, TableInsightReader.RowCap, "Mindestanzahl der Aufrufe");
		if (query["path"]?.Length > 2048 || query["originHost"]?.Length > 253) throw new QueryException("Der Seiten- oder Herkunftsfilter ist zu lang.");
		return new(query["path"], query["originHost"]?.Trim().ToLowerInvariant(), device, reload, min);
	}
	public static int Integer(string? raw, int fallback, int min, int max, string label) => raw == null ? fallback : int.TryParse(raw, out var value) && value >= min && value <= max ? value : throw new QueryException($"{label}: Bitte eine Zahl zwischen {min} und {max} angeben.");
	public bool Matches(List<PageViewEntity> rows) => rows.Count >= MinViews &&
		(HasReload == null || rows.Any(r => r.NavigationType == "reload") == HasReload) &&
		rows.Any(r => (Path == null || InsightValues.Path(r) == Path) && (OriginHost == null || InsightValues.Origin(r.ReferrerHost) == OriginHost) && (Device == null || InsightValues.Device(r.ViewportWidth) == Device));
}

public sealed class SessionHandles(string secret)
{
	private readonly byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("dashboard-session-handles-v1:" + secret));
	public string Create(string id, InsightRange range) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { range.Start, range.End, id }))));
}

public sealed record SessionSummary(string SessionRef, string SessionId, string VisitorId, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, int ViewCount, int DistinctPathCount, string EntryPath, string LastPath, int ReloadCount, string DeviceCategory, string[] OriginHosts, [property: JsonIgnore] string Position);

public sealed class SessionSnapshots
{
	public sealed record Snapshot(string Id, string Query, SessionSummary[] Items, bool Truncated, int WithoutSessionId, DateTimeOffset GeneratedAt);
	private readonly Dictionary<string, Snapshot> snapshots = new();
	private readonly object gate = new();
	public Snapshot Add(string query, SessionSummary[] items, bool truncated, int missing)
	{
		lock (gate)
		{
			foreach (var old in snapshots.Values.Where(s => s.GeneratedAt < DateTimeOffset.UtcNow.AddMinutes(-5)).ToList()) snapshots.Remove(old.Id);
			while (snapshots.Count > 0 && (snapshots.Count >= 20 || snapshots.Values.Sum(s => s.Items.Length) + items.Length > TableInsightReader.RowCap)) snapshots.Remove(snapshots.Values.MinBy(s => s.GeneratedAt)!.Id);
			var result = new Snapshot(Guid.NewGuid().ToString("N"), query, items, truncated, missing, DateTimeOffset.UtcNow);
			snapshots.Add(result.Id, result);
			return result;
		}
	}
	public (Snapshot Snapshot, string Position) Resolve(string cursor, string query)
	{
		if (cursor.Length > 1024) throw new QueryException("Ungültige Fortsetzung.");
		string[] parts;
		try { parts = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(cursor)) ?? []; }
		catch (Exception ex) when (ex is FormatException or JsonException) { throw new QueryException("Ungültige Fortsetzung."); }
		if (parts.Length != 2) throw new QueryException("Ungültige Fortsetzung.");
		lock (gate)
		{
			if (!snapshots.TryGetValue(parts[0], out var snapshot) || snapshot.GeneratedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
				throw new QueryException("Die Sitzungsliste ist abgelaufen. Bitte die Suche neu starten.", HttpStatusCode.Gone);
			if (snapshot.Query != query || !snapshot.Items.Any(s => s.Position == parts[1])) throw new QueryException("Die Fortsetzung passt nicht zu diesen Filtern.");
			return (snapshot, parts[1]);
		}
	}
	public static string Cursor(Snapshot snapshot, SessionSummary last) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { snapshot.Id, last.Position }));
}

public class GetPageViewSessions(IInsightReader reader, SessionHandles handles, SessionSnapshots snapshots)
{
	[Function("get-pageview-sessions")]
	public Task<HttpResponseData> Run(
		[HttpTrigger(AuthorizationLevel.Function, "get", Route = "pageviews/sessions")] HttpRequestData request,
		CancellationToken cancellationToken) => InsightHttp.Run(request, async () =>
	{
		var range = InsightRange.Parse(request.Query, 92);
		var filters = SessionFilters.Parse(request.Query);
		var limit = SessionFilters.Integer(request.Query["limit"], 25, 1, 100, "Seitengröße");
		var query = JsonSerializer.Serialize(new { range.Start, range.End, filters });
		SessionSnapshots.Snapshot snapshot;
		string? position = null;
		if (request.Query["cursor"] is { } cursor) (snapshot, position) = snapshots.Resolve(cursor, query);
		else
		{
			var scan = await reader.ReadAsync(range, cancellationToken);
			var items = scan.Rows.Where(r => !string.IsNullOrWhiteSpace(r.SessionId)).GroupBy(r => r.SessionId!, StringComparer.Ordinal)
				.Select(g => InsightValues.Ordered(g).ToList()).Where(filters.Matches).Select(rows => Summarize(rows, range))
				.OrderByDescending(s => s.LastSeen).ThenBy(s => s.Position, StringComparer.Ordinal).ToArray();
			snapshot = snapshots.Add(query, items, scan.Truncated, scan.Rows.Count(r => string.IsNullOrWhiteSpace(r.SessionId)));
		}
		var remaining = position == null ? snapshot.Items : snapshot.Items.SkipWhile(s => s.Position != position).Skip(1);
		var page = remaining.Take(limit + 1).ToArray();
		return new { range = range.Metadata, snapshot.GeneratedAt, snapshot.Truncated, snapshot.WithoutSessionId, totalSessions = snapshot.Items.Length, items = page.Take(limit), nextCursor = page.Length > limit ? SessionSnapshots.Cursor(snapshot, page[limit - 1]) : null };
	}, cancellationToken);

	public SessionSummary Summarize(List<PageViewEntity> rows, InsightRange range)
	{
		var first = rows[0];
		var last = rows[^1];
		return new(handles.Create(first.SessionId!, range), InsightValues.Mask(first.SessionId), InsightValues.Mask(first.VisitorId), first.Timestamp, last.Timestamp, rows.Count, rows.Select(InsightValues.Path).Distinct().Count(), InsightValues.Path(first), InsightValues.Path(last), rows.Count(r => r.NavigationType == "reload"), InsightValues.Device(first.ViewportWidth), rows.Select(r => InsightValues.Origin(r.ReferrerHost)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray(), handles.Create(JsonSerializer.Serialize(new[] { last.PartitionKey, last.RowKey }), range));
	}
}

public class GetPageViewSession(IInsightReader reader, SessionHandles handles)
{
	[Function("get-pageview-session")]
	public Task<HttpResponseData> Run(
		[HttpTrigger(AuthorizationLevel.Function, "get", Route = "pageviews/sessions/{sessionId}")] HttpRequestData request,
		string sessionId,
		CancellationToken cancellationToken) => InsightHttp.Run(request, async () =>
	{
		var range = InsightRange.Parse(request.Query, 92);
		if (sessionId.Length != 64 || !sessionId.All(char.IsAsciiHexDigit)) throw new QueryException("Ungültiger Sitzungsverweis.");
		var scan = await reader.ReadAsync(range, cancellationToken);
		var matching = scan.Rows.Where(r => !string.IsNullOrWhiteSpace(r.SessionId)).GroupBy(r => r.SessionId!, StringComparer.Ordinal).FirstOrDefault(g => handles.Create(g.Key, range) == sessionId);
		var rows = InsightValues.Ordered(matching?.AsEnumerable() ?? []).ToList();
		if (rows.Count == 0) throw new QueryException(scan.Truncated ? "Die Suche ist unvollständig. Bitte den Zeitraum verkleinern." : "Keine Sitzung in diesem Zeitraum gefunden.", scan.Truncated ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
		return new
		{
			range = range.Metadata,
			sessionRef = sessionId,
			sessionId = InsightValues.Mask(rows[0].SessionId),
			visitorId = InsightValues.Mask(rows[0].VisitorId),
			generatedAt = DateTimeOffset.UtcNow,
			truncated = scan.Truncated,
			possiblyTruncatedStart = scan.Truncated || InsightRange.LocalDate(rows[0].Timestamp!.Value) == range.Start,
			possiblyTruncatedEnd = scan.Truncated || InsightRange.LocalDate(rows[^1].Timestamp!.Value) == range.End,
			events = rows.Select((r, i) => new { path = InsightValues.Path(r), referrerHost = r.ReferrerHost, navigationType = InsightValues.Classified(r) ? r.NavigationType : "unknown", viewportWidth = r.ViewportWidth, deviceCategory = InsightValues.Device(r.ViewportWidth), observedAt = r.Timestamp, gapSeconds = i == 0 ? (double?)null : Math.Max(0, (r.Timestamp!.Value - rows[i - 1].Timestamp!.Value).TotalSeconds) }),
		};
	}, cancellationToken);
}
