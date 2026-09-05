using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using DashboardApi.Shared.Entities;
using Microsoft.Azure.Functions.Worker.Http;

namespace DashboardApi.Features.PageViews;

public sealed class QueryException(string message, HttpStatusCode status = HttpStatusCode.BadRequest) : Exception(message)
{
	public HttpStatusCode Status { get; } = status;
}

public sealed record InsightRange(DateOnly Start, DateOnly End)
{
	public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
	public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);
	public int Days => End.DayNumber - Start.DayNumber + 1;
	public DateTimeOffset UtcStart => Midnight(Start);
	public DateTimeOffset UtcEnd => Midnight(End.AddDays(1));
	public object Metadata => new { start = Start, end = End, timezone = Zone.Id };
	public InsightRange Previous => new(Start.AddDays(-Days), Start.AddDays(-1));
	public static DateOnly LocalDate(DateTimeOffset time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, Zone).DateTime);
	private static DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Zone));
	public static InsightRange Parse(NameValueCollection query, int maxDays)
	{
		if (!DateOnly.TryParseExact(query["start"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
			!DateOnly.TryParseExact(query["end"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
			throw new QueryException("Start und Ende sind als Datum (JJJJ-MM-TT) erforderlich.");
		var range = new InsightRange(start, end);
		if (end < start) throw new QueryException("Das Ende darf nicht vor dem Start liegen.");
		if (start < Today.AddMonths(-36) || end > Today) throw new QueryException("Der Zeitraum muss innerhalb der letzten 36 Monate bis heute liegen.");
		if (range.Days > maxDays) throw new QueryException($"Dieser Zeitraum darf höchstens {maxDays} Tage umfassen.");
		return range;
	}
}

public static class InsightValues
{
	public static readonly string[] Devices = ["Unbekannt", "Mobil", "Tablet", "Laptop", "Breitbild"];
	public static string Device(int width) => width switch { <= 0 => "Unbekannt", < 768 => "Mobil", < 1024 => "Tablet", < 1440 => "Laptop", _ => "Breitbild" };
	public static string Path(PageViewEntity row) => string.IsNullOrWhiteSpace(row.Path) ? "(unbekannt)" : row.Path;
	public static string? Origin(string? host)
	{
		var value = host?.Trim().ToLowerInvariant();
		return string.IsNullOrEmpty(value) || value is "liedertafel-mining.at" or "www.liedertafel-mining.at" or "dashboard.liedertafel-mining.at" or "liedertafel.at" or "www.liedertafel.at" || value.EndsWith(".azurestaticapps.net", StringComparison.Ordinal) ? null : value;
	}
	public static bool Classified(PageViewEntity row) => row.NavigationType is "navigate" or "reload" or "back_forward";
	public static string Mask(string? id) => string.IsNullOrWhiteSpace(id) ? "unbekannt" : id[..Math.Min(8, id.Length / 2)] + "…";
	public static IOrderedEnumerable<PageViewEntity> Ordered(IEnumerable<PageViewEntity> rows) => rows.OrderBy(r => r.Timestamp).ThenBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal);
}

public static class InsightHttp
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	public static async Task<HttpResponseData> Run(HttpRequestData request, Func<Task<object>> action, CancellationToken ct)
	{
		object body;
		var status = HttpStatusCode.OK;
		try { body = await action(); }
		catch (QueryException ex) { status = ex.Status; body = new { error = ex.Message }; }
		var response = request.CreateResponse(status);
		response.Headers.Add("Content-Type", "application/json; charset=utf-8");
		response.Headers.Add("Cache-Control", "no-store");
		await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
		return response;
	}
}

public sealed record ScanResult(IReadOnlyList<PageViewEntity> Rows, bool Truncated);

public interface IInsightReader
{
	Task<ScanResult> ReadAsync(InsightRange range, CancellationToken ct);
}

public sealed class TableInsightReader(TableServiceClient client) : IInsightReader
{
	public const int RowCap = 200_000;
	public async Task<ScanResult> ReadAsync(InsightRange range, CancellationToken ct)
	{
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
		budget.CancelAfter(TimeSpan.FromSeconds(30));
		var utcStart = range.UtcStart;
		var utcEnd = range.UtcEnd;
		var start = $"Pv|{utcStart:yyyy-MM-dd}";
		var end = $"Pv|{utcEnd.AddTicks(-1):yyyy-MM-dd}";
		var filter = TableClient.CreateQueryFilter($"PartitionKey ge {start} and PartitionKey le {end}");
		var rows = new List<PageViewEntity>();
		var scanned = 0;
		try
		{
			await foreach (var row in client.GetTableClient("pageviews").QueryAsync<PageViewEntity>(filter, maxPerPage: 1000, cancellationToken: budget.Token).WithCancellation(budget.Token))
			{
				if (++scanned > RowCap) return new(rows, true);
				if (row.Timestamp >= utcStart && row.Timestamp < utcEnd) rows.Add(row);
			}
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(rows, true); }
		return new(rows, false);
	}
}
