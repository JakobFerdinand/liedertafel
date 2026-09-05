using System.Collections.Specialized;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using DashboardApi.Features.PageViews;
using DashboardApi.Shared.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

var checks = 0;
void Check(bool condition, string name)
{
	if (!condition) throw new Exception($"FAIL: {name}");
	checks++;
	Console.WriteLine($"PASS: {name}");
}
var today = InsightRange.Today;
var range = new InsightRange(today.AddDays(-6), today);
var query = $"start={range.Start:yyyy-MM-dd}&end={range.End:yyyy-MM-dd}";
var spring = new InsightRange(new(2026, 3, 29), new(2026, 3, 29));
var autumn = new InsightRange(new(2025, 10, 26), new(2025, 10, 26));
Check((spring.UtcEnd - spring.UtcStart).TotalHours == 23, "Vienna spring DST day has 23 hours");
Check((autumn.UtcEnd - autumn.UtcStart).TotalHours == 25, "Vienna autumn DST day has 25 hours");
Check(range.Previous.Days == 7 && range.Previous.End == range.Start.AddDays(-1), "previous period is adjacent and equal length");

PageViewEntity Row(string session, string path, DateTimeOffset time, string key, string? nav = "navigate", int width = 1200, string? origin = null, string visitor = "visitor-abcdefgh") => new()
{
	SessionId = session, VisitorId = visitor, Path = path, Timestamp = time, RowKey = key, PartitionKey = $"Pv|{time:yyyy-MM-dd}", NavigationType = nav, ViewportWidth = width, ReferrerHost = origin,
};
var rows = new List<PageViewEntity>
{
	Row("session-abcdefgh", "/last", range.UtcEnd.AddMinutes(-1), "a", "reload", 1200, "example.com"),
	Row("session-abcdefgh", "/first", range.UtcStart.AddMinutes(1), "z", null, 0),
	Row("session-otherxyz", "/other", range.UtcStart.AddDays(1), "b", "navigate", 400, "www.liedertafel.at"),
	Row("", "/legacy", range.UtcStart.AddDays(2), "c", null, 0, visitor: ""),
};
var reader = new FakeReader(rows);
var handles = new SessionHandles("test-only-key");
var snapshots = new SessionSnapshots();
var stats = new GetPageViewStats(reader);
var sessions = new GetPageViewSessions(reader, handles, snapshots);
var detail = new GetPageViewSession(reader, handles);
async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseData Response)> Read(Task<HttpResponseData> action)
{
	var response = await action;
	response.Body.Position = 0;
	using var json = await JsonDocument.ParseAsync(response.Body);
	return (response.StatusCode, json.RootElement.Clone(), response);
}
var result = await Read(stats.Run(new Request($"stats?{query}"), default));
Check(result.Status == HttpStatusCode.OK, "stats endpoint returns success");
var current = result.Body.GetProperty("current");
Check(current.GetProperty("total").GetInt32() == 4 && current.GetProperty("withoutSessionId").GetInt32() == 1, "stats includes missing session IDs visibly");
Check(current.GetProperty("series").GetArrayLength() == 7 && current.GetProperty("series")[6].GetProperty("partial").GetBoolean(), "daily series zero-filled and today partial");
Check(current.GetProperty("devices")[0].GetProperty("device").GetString() == "Unbekannt" && current.GetProperty("devices")[0].GetProperty("count").GetInt32() == 2, "zero screen widths are unknown");
Check(current.GetProperty("classifiedViews").GetInt32() == 2 && current.GetProperty("reloads").GetInt32() == 1, "reload denominators preserve unclassified rows");
Check(current.GetProperty("origins").GetArrayLength() == 1, "internal origins excluded");
Check(current.GetProperty("pagesPerSession").GetDouble() == 1.5, "pages per session excludes missing IDs");
foreach (var invalid in new[] { "days=7", $"start={today:yyyy-MM-dd}&end={today.AddDays(-1):yyyy-MM-dd}", query + "&granularity=month", query + "&compare=invalid", $"start={today.AddDays(-92):yyyy-MM-dd}&end={today:yyyy-MM-dd}", $"start={today.AddMonths(-36):yyyy-MM-dd}&end={today.AddMonths(-36):yyyy-MM-dd}" })
{
	var failure = await Read(stats.Run(new Request($"stats?{invalid}"), default));
	Check(failure.Status == HttpStatusCode.BadRequest && failure.Body.GetProperty("error").GetString()!.Length > 0, $"reject invalid stats query: {invalid}");
}
var noCompare = await Read(stats.Run(new Request($"stats?{query}&compare=none"), default));
Check(noCompare.Body.GetProperty("previous").ValueKind == JsonValueKind.Null, "comparison can be disabled");
var page1 = await Read(sessions.Run(new Request($"sessions?{query}&limit=1"), default));
Check(page1.Status == HttpStatusCode.OK && page1.Body.GetProperty("totalSessions").GetInt32() == 2, "session list groups complete sessions");
Check(page1.Response.Headers.GetValues("Cache-Control").Single() == "no-store", "session list is no-store");
var first = page1.Body.GetProperty("items")[0];
var sessionRef = first.GetProperty("sessionRef").GetString()!;
Check(first.GetProperty("viewCount").GetInt32() == 2 && first.GetProperty("entryPath").GetString() == "/first" && first.GetProperty("deviceCategory").GetString() == "Unbekannt", "summary order uses timestamps, not row keys");
Check(!page1.Body.GetRawText().Contains("session-abcdefgh") && !page1.Body.GetRawText().Contains("visitor-abcdefgh") && !page1.Body.GetRawText().Contains("position"), "default response masks IDs and hides storage positions");
Check(handles.Create("session-abcdefgh", range.Previous) != sessionRef, "handles are range-bound");
var calls = reader.Calls;
var cursor = page1.Body.GetProperty("nextCursor").GetString()!;
var page2 = await Read(sessions.Run(new Request($"sessions?{query}&limit=1&cursor={Uri.EscapeDataString(cursor)}"), default));
Check(reader.Calls == calls && page2.Body.GetProperty("items")[0].GetProperty("entryPath").GetString() == "/other" && page2.Body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null, "next page uses stable snapshot without storage read");
var wrongFilter = await Read(sessions.Run(new Request($"sessions?{query}&path=%2Ffirst&cursor={Uri.EscapeDataString(cursor)}"), default));
Check(wrongFilter.Status == HttpStatusCode.BadRequest, "cursor cannot be reused with different filters");
var expired = await Read(new GetPageViewSessions(reader, handles, new SessionSnapshots()).Run(new Request($"sessions?{query}&cursor={Uri.EscapeDataString(cursor)}"), default));
Check(expired.Status == HttpStatusCode.Gone, "missing snapshot explicitly requires restart");
var timeline = await Read(detail.Run(new Request($"sessions/{sessionRef}?{query}"), sessionRef, default));
var events = timeline.Body.GetProperty("events");
Check(events[0].GetProperty("path").GetString() == "/first" && events[1].GetProperty("path").GetString() == "/last", "timeline orders chronological observations");
Check(events[0].GetProperty("navigationType").GetString() == "unknown" && events[0].GetProperty("gapSeconds").ValueKind == JsonValueKind.Null && events[1].GetProperty("gapSeconds").GetDouble() == (rows[0].Timestamp - rows[1].Timestamp)!.Value.TotalSeconds, "timeline marks unknown navigation and calculates observed gaps");
Check(timeline.Body.GetProperty("possiblyTruncatedStart").GetBoolean() && timeline.Body.GetProperty("possiblyTruncatedEnd").GetBoolean(), "timeline flags both window edges");
Check(timeline.Response.Headers.GetValues("Cache-Control").Single() == "no-store" && !timeline.Body.GetRawText().Contains("session-abcdefgh"), "timeline masks IDs and disables caching");
var filtered = await Read(sessions.Run(new Request($"sessions?{query}&path=%2Ffirst&device=Unbekannt&hasReload=true&minViews=2"), default));
Check(filtered.Body.GetProperty("items").GetArrayLength() == 1 && filtered.Body.GetProperty("items")[0].GetProperty("viewCount").GetInt32() == 2, "segment filters retain full session summary");
reader.Truncated = true;
var truncated = await Read(stats.Run(new Request($"stats?{query}"), default));
Check(truncated.Body.GetProperty("truncated").GetBoolean(), "storage cap propagates to stats");
var missing = await Read(detail.Run(new Request($"sessions/{new string('a', 64)}?{query}"), new string('a', 64), default));
Check(missing.Status == HttpStatusCode.ServiceUnavailable, "truncated detail search does not claim definite absence");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await stats.Run(new Request($"stats?{query}"), cancelled.Token); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { Check(true, "request cancellation reaches reader"); }

var cappedReader = new TableInsightReader(new CappedTableService(rows[0]));
var capped = await cappedReader.ReadAsync(range, default);
Check(capped.Truncated && capped.Rows.Count == TableInsightReader.RowCap, "real reader enforces row cap during SDK enumeration");
var weekResult = await Read(stats.Run(new Request($"stats?{query}&granularity=week&compare=none"), default));
Check(weekResult.Body.GetProperty("current").GetProperty("series").EnumerateArray().All(p => DateOnly.Parse(p.GetProperty("bucketStart").GetString()!).DayOfWeek == DayOfWeek.Monday), "weekly buckets start Monday in Vienna");
var boundaryRows = new[] { Row("edge-one", "/at-start", range.UtcStart, "edge1"), Row("edge-two", "/before-start", range.UtcStart.AddTicks(-1), "edge2"), Row("edge-three", "/at-end", range.UtcEnd, "edge3") };
var boundaryStats = JsonSerializer.SerializeToElement(GetPageViewStats.Aggregate(boundaryRows, range, "day"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(boundaryStats.GetProperty("total").GetInt32() == 1, "Vienna window includes exact start and excludes exact next midnight");
foreach (var invalid in new[] { "limit=101", "device=invalid", "hasReload=maybe", "minViews=0", "cursor=bad-token" })
{
	var failure = await Read(sessions.Run(new Request($"sessions?{query}&{invalid}"), default));
	Check(failure.Status == HttpStatusCode.BadRequest, $"session validation rejects {invalid}");
}
if (args.Contains("--fixtures"))
{
	var index = Array.IndexOf(args, "--fixtures");
	var directory = index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("--fixtures requires an output directory");
	Directory.CreateDirectory(directory);
	await File.WriteAllTextAsync(Path.Combine(directory, "stats.json"), result.Body.GetRawText());
	await File.WriteAllTextAsync(Path.Combine(directory, "sessions.json"), page1.Body.GetRawText());
	await File.WriteAllTextAsync(Path.Combine(directory, "sessions-next.json"), page2.Body.GetRawText());
	await File.WriteAllTextAsync(Path.Combine(directory, "detail.json"), timeline.Body.GetRawText());
}

if (args.Contains("--azurite"))
{
	var client = new TableServiceClient("UseDevelopmentStorage=true");
	var table = client.GetTableClient("pageviews");
	await table.CreateIfNotExistsAsync();
	var partition = $"Pv|{DateTimeOffset.UtcNow:yyyy-MM-dd}";
	var rowKey = "insights-check-" + Guid.NewGuid().ToString("N");
	await table.AddEntityAsync(new PageViewEntity { PartitionKey = partition, RowKey = rowKey, Path = "/insights-check", SessionId = "test-session" });
	try
	{
		var scanned = await new TableInsightReader(client).ReadAsync(new(today, today), default);
		Check(scanned.Rows.Any(r => r.RowKey == rowKey) && !scanned.Truncated, "Azurite query reads real table timestamps and Vienna partition range");
		try { await new TableInsightReader(client).ReadAsync(range, cancelled.Token); throw new Exception("Expected cancellation"); }
		catch (OperationCanceledException) { Check(true, "Azure SDK enumeration honors cancelled token"); }
	}
	finally { await table.DeleteEntityAsync(partition, rowKey, ETag.All); }
}
Console.WriteLine($"{checks} checks passed.");

sealed class FakeReader(IReadOnlyList<PageViewEntity> rows) : IInsightReader
{
	public int Calls { get; private set; }
	public bool Truncated { get; set; }
	public Task<ScanResult> ReadAsync(InsightRange range, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested(); Calls++;
		return Task.FromResult(new ScanResult(rows.Where(r => r.Timestamp >= range.UtcStart && r.Timestamp < range.UtcEnd).ToList(), Truncated));
	}
}
sealed class Request(string route) : HttpRequestData(new TestContext())
{
	public override Stream Body { get; } = new MemoryStream();
	public override HttpHeadersCollection Headers { get; } = new();
	public override IReadOnlyCollection<IHttpCookie> Cookies => [];
	public override Uri Url { get; } = new("http://localhost/api/pageviews/" + route);
	public override IEnumerable<ClaimsIdentity> Identities => [];
	public override string Method => "GET";
	public override HttpResponseData CreateResponse() => new Response(FunctionContext);
}
sealed class Response(FunctionContext context) : HttpResponseData(context)
{
	public override HttpStatusCode StatusCode { get; set; }
	public override HttpHeadersCollection Headers { get; set; } = new();
	public override Stream Body { get; set; } = new MemoryStream();
	public override HttpCookies Cookies => null!;
}
sealed class TestContext : FunctionContext
{
	public override string InvocationId => "test";
	public override string FunctionId => "test";
	public override TraceContext TraceContext => null!;
	public override BindingContext BindingContext => null!;
	public override RetryContext RetryContext => null!;
	public override IServiceProvider InstanceServices { get; set; } = null!;
	public override FunctionDefinition FunctionDefinition => null!;
	public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
	public override IInvocationFeatures Features => null!;
}

sealed class CappedTableService(PageViewEntity row) : TableServiceClient
{
	public override TableClient GetTableClient(string tableName) => new CappedTable(row);
}
sealed class CappedTable(PageViewEntity row) : TableClient
{
	public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
	{
	 var values = Enumerable.Repeat((T)(object)row, 1000).ToArray();
	 var pages = Enumerable.Range(0, 201).Select(i => Page<T>.FromValues(values, i < 200 ? "next" : null, null!));
	 return AsyncPageable<T>.FromPages(pages);
	}
}
