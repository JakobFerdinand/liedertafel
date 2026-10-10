using System.Collections.Specialized;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using DashboardApi.Features.PageViews;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PageViewStorage;
using WebsiteApi.Features.PageViews;

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

PageViewEntity Row(string session, string path, DateTimeOffset time, string key, string? nav = "navigate", int width = 1200, string? origin = null, string visitor = "visitor-abcdefgh")
{
	var row = PageViewEntity.Create(time, path, origin, width, session, visitor, nav);
	row.RowKey = key;
	row.Timestamp = time;
	return row;
}
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
// Writer/reader round trip: the real website-api handler and store write into an in-memory table,
// the real dashboard-api reader scans it. Both only agree through the shared PageViewStorage module.
var service = new InMemoryTableService();
service.Table.Seed(new TableEntity("Pv|2000-01-01", "expired"));
var writer = new PageView.Handler(new PageView.TablePageViewStore(service, NullLogger<PageView.TablePageViewStore>.Instance));
var written = new PageView.Payload("/round-trip", "example.com", 1200, "round-trip-session", "round-trip-visitor", "reload");
Check(writer.Validate(written) is null, "writer accepts the round-trip payload");
await writer.SaveAsync(written, default);
var roundTrip = await new TableInsightReader(service).ReadAsync(new(today, today), default);
var stored = roundTrip.Rows.SingleOrDefault(r => r.SessionId == "round-trip-session");
Check(stored is not null && !roundTrip.Truncated, "reader finds the row the writer stored");
Check(stored!.Path == "/round-trip" && stored.ReferrerHost == "example.com" && stored.ViewportWidth == 1200 && stored.VisitorId == "round-trip-visitor" && stored.NavigationType == "reload", "round trip preserves every stored property");
Check(stored.PartitionKey == $"Pv|{DateTime.UtcNow:yyyy-MM-dd}" && Guid.TryParse(stored.RowKey, out _), "writer stores a UTC-day partition and a GUID row key");
Check(service.Table.Contains("Cleanup", "last") && !service.Table.Contains("Pv|2000-01-01", "expired") && service.Table.Contains(stored.PartitionKey, stored.RowKey), "retention removes expired partitions and keeps the cleanup marker and fresh rows");
var roundTripStats = await Read(new GetPageViewStats(new TableInsightReader(service)).Run(new Request($"stats?start={today:yyyy-MM-dd}&end={today:yyyy-MM-dd}&compare=none"), default));
Check(roundTripStats.Body.GetProperty("current").GetProperty("total").GetInt32() == 1 && roundTripStats.Body.GetProperty("current").GetProperty("reloads").GetInt32() == 1, "stats endpoint reports the stored row");
foreach (var type in NavigationTypes.All) Check(writer.Validate(written with { NavigationType = type }) is null && InsightValues.Classified(Row("s", "/", DateTimeOffset.UtcNow, "k", type)), $"navigation type {type} is accepted and classified");
Check(writer.Validate(written with { NavigationType = "prerender" }) is { } navigationError && navigationError.Contains("'navigate', 'reload', 'back_forward'") && !InsightValues.Classified(Row("s", "/", DateTimeOffset.UtcNow, "k", "prerender")), "unknown navigation types are rejected and unclassified");
foreach (var day in new[] { spring, autumn })
{
	var edge = new InMemoryTableService();
	foreach (var (label, instant) in new[] { ("before", day.UtcStart.AddTicks(-1)), ("start", day.UtcStart), ("last", day.UtcEnd.AddTicks(-1)), ("after", day.UtcEnd) })
	{
		var entity = PageViewEntity.Create(instant, "/" + label, null, 1200, "edge-" + label, null, null);
		entity.Timestamp = instant;
		await PageViewTable.Client(edge).AddEntityAsync(entity);
	}
	var edgeRows = (await new TableInsightReader(edge).ReadAsync(day, default)).Rows;
	Check(edgeRows.Select(r => r.Path).Order().SequenceEqual(["/last", "/start"]), $"reader scans the UTC partitions covering Vienna day {day.Start:yyyy-MM-dd}");
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
	var partition = PageViewTable.PartitionKey(DateTimeOffset.UtcNow);
	var rowKey = "insights-check-" + Guid.NewGuid().ToString("N");
	var storedSession = "azurite-" + Guid.NewGuid().ToString("N");
	await table.AddEntityAsync(new PageViewEntity { PartitionKey = partition, RowKey = rowKey, Path = "/insights-check", SessionId = "test-session" });
	try
	{
		await new PageView.Handler(new PageView.TablePageViewStore(client, NullLogger<PageView.TablePageViewStore>.Instance)).SaveAsync(new("/azurite-round-trip", null, 800, storedSession, null, "navigate"), default);
		var azuriteRows = (await new TableInsightReader(client).ReadAsync(new(today, today), default)).Rows;
		var azuriteStored = azuriteRows.SingleOrDefault(r => r.SessionId == storedSession);
		Check(azuriteStored is { Path: "/azurite-round-trip", ViewportWidth: 800, NavigationType: "navigate" }, "Azurite round trip: website-api store writes what the dashboard reader reads");
		await table.DeleteEntityAsync(azuriteStored!.PartitionKey, azuriteStored.RowKey, ETag.All);
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
		return Task.FromResult(new ScanResult(rows.Where(r => r.IsWithin(range.UtcStart, range.UtcEnd)).ToList(), Truncated));
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

sealed class InMemoryTableService : TableServiceClient
{
	public InMemoryTable Table { get; } = new();
	public override TableClient GetTableClient(string tableName) => tableName == PageViewTable.Name ? Table : throw new InvalidOperationException($"Unexpected table {tableName}");
}
sealed class InMemoryTable : TableClient
{
	private readonly List<ITableEntity> entities = [];
	public void Seed(ITableEntity entity) => entities.Add(entity);
	public bool Contains(string partitionKey, string rowKey) => entities.Any(e => e.PartitionKey == partitionKey && e.RowKey == rowKey);
	public override Task<Azure.Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult<Azure.Response<TableItem>>(null!);
	public override Task<Azure.Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
	{
		entity.Timestamp ??= DateTimeOffset.UtcNow;
		entities.Add(entity);
		return Task.FromResult<Azure.Response>(null!);
	}
	public override Task<Azure.Response> UpsertEntityAsync<T>(T entity, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
	{
		entities.RemoveAll(e => e.PartitionKey == entity.PartitionKey && e.RowKey == entity.RowKey);
		entity.Timestamp = DateTimeOffset.UtcNow;
		entities.Add(entity);
		return Task.FromResult<Azure.Response>(null!);
	}
	public override Task<Azure.Response<T>> GetEntityAsync<T>(string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default) =>
		entities.OfType<T>().FirstOrDefault(e => e.PartitionKey == partitionKey && e.RowKey == rowKey) is { } found
			? Task.FromResult(Azure.Response.FromValue(found, null!))
			: throw new RequestFailedException(404, "Entity not found.");
	public override Task<Azure.Response<IReadOnlyList<Azure.Response>>> SubmitTransactionAsync(IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
	{
		foreach (var action in transactionActions) entities.RemoveAll(e => e.PartitionKey == action.Entity.PartitionKey && e.RowKey == action.Entity.RowKey);
		return Task.FromResult<Azure.Response<IReadOnlyList<Azure.Response>>>(null!);
	}
	public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
	{
		// Understands only the partition-key bounds the pageview module generates.
		var bounds = System.Text.RegularExpressions.Regex.Matches(filter ?? "", @"PartitionKey (ge|gt|le|lt) '([^']*)'");
		bool Matches(string partitionKey) => bounds.All(b => (b.Groups[1].Value, string.CompareOrdinal(partitionKey, b.Groups[2].Value)) is ("ge", >= 0) or ("gt", > 0) or ("le", <= 0) or ("lt", < 0));
		var values = entities.OfType<T>().Where(e => Matches(e.PartitionKey)).ToArray();
		return AsyncPageable<T>.FromPages([Page<T>.FromValues(values, null, null!)]);
	}
}
