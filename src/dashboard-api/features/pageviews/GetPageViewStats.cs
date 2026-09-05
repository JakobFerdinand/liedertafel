using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using DashboardApi.Shared.Entities;

namespace DashboardApi.Features.PageViews;

public class GetPageViewStats(IInsightReader reader)
{
	[Function("get-pageview-stats")]
	public Task<HttpResponseData> Run(
		[HttpTrigger(AuthorizationLevel.Function, "get", Route = "pageviews/stats")] HttpRequestData request,
		CancellationToken cancellationToken) => InsightHttp.Run(request, async () =>
	{
		var granularity = request.Query["granularity"] ?? "day";
		if (granularity is not ("day" or "week")) throw new QueryException("Die Auflösung muss Tag (day) oder Woche (week) sein.");
		var compare = request.Query["compare"] ?? "previous_period";
		if (compare is not ("previous_period" or "none")) throw new QueryException("Der Vergleich muss previous_period oder none sein.");
		var range = InsightRange.Parse(request.Query, granularity == "day" ? 92 : 400);
		if (compare == "previous_period" && range.Previous.Start < InsightRange.Today.AddMonths(-36))
			throw new QueryException("Die Vorperiode liegt außerhalb der letzten 36 Monate. Bitte den Vergleich ausschalten.");
		var scan = await reader.ReadAsync(compare == "previous_period" ? new(range.Previous.Start, range.End) : range, cancellationToken);
		return new
		{
			range = range.Metadata,
			granularity,
			generatedAt = DateTimeOffset.UtcNow,
			truncated = scan.Truncated,
			current = Aggregate(scan.Rows, range, granularity),
			previous = compare == "previous_period" ? Aggregate(scan.Rows, range.Previous, granularity) : null,
		};
	}, cancellationToken);

	public sealed record Count(string Name, int Value);
	public sealed record Point(DateOnly BucketStart, int Count, bool Partial, int Sessions, int UniqueVisitors, int UniquePaths, double PagesPerSession, int Reloads);
	public sealed record SegmentPoint(DateOnly BucketStart, string Name, int Count, bool Partial);

	public static object Aggregate(IReadOnlyList<PageViewEntity> all, InsightRange range, string granularity)
	{
		var rows = all.Where(r => r.Timestamp >= range.UtcStart && r.Timestamp < range.UtcEnd).ToList();
		DateOnly Bucket(DateOnly date) => granularity == "day" ? date : date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
		var buckets = new List<DateOnly>();
		for (var date = Bucket(range.Start); date <= range.End; date = date.AddDays(granularity == "day" ? 1 : 7)) buckets.Add(date);
		var grouped = rows.GroupBy(r => Bucket(InsightRange.LocalDate(r.Timestamp!.Value))).ToDictionary(g => g.Key, g => g.ToList());
		bool Partial(DateOnly date) => date < range.Start || date.AddDays(granularity == "day" ? 0 : 6) > range.End || (range.End == InsightRange.Today && date == Bucket(range.End));
		static int Sessions(IEnumerable<PageViewEntity> values) => values.Where(r => !string.IsNullOrWhiteSpace(r.SessionId)).Select(r => r.SessionId).Distinct().Count();
		static int Visitors(IEnumerable<PageViewEntity> values) => values.Where(r => !string.IsNullOrWhiteSpace(r.VisitorId)).Select(r => r.VisitorId).Distinct().Count();
		static double PagesPerSession(List<PageViewEntity> values) => Sessions(values) is var count && count > 0 ? Math.Round((double)values.Count(r => !string.IsNullOrWhiteSpace(r.SessionId)) / count, 1) : 0;
		static List<Count> Counts(IEnumerable<PageViewEntity> values, Func<PageViewEntity, string?> key) => values.Select(key).Where(k => k != null).GroupBy(k => k!).Select(g => new Count(g.Key, g.Count())).OrderByDescending(c => c.Value).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
		var paths = Counts(rows, InsightValues.Path);
		var origins = Counts(rows, r => InsightValues.Origin(r.ReferrerHost));
		var devices = InsightValues.Devices.Select(d => new Count(d, rows.Count(r => InsightValues.Device(r.ViewportWidth) == d))).ToList();
		List<SegmentPoint> Segments(IEnumerable<string> names, Func<PageViewEntity, string?> key) => buckets.SelectMany(b => names.Select(n => new SegmentPoint(b, (string)n, (grouped.GetValueOrDefault(b) ?? []).Count(r => key(r) == n), Partial(b)))).ToList();
		var firstVisitorBucket = rows.Where(r => !string.IsNullOrWhiteSpace(r.VisitorId)).GroupBy(r => r.VisitorId!).ToDictionary(g => g.Key, g => g.Min(r => Bucket(InsightRange.LocalDate(r.Timestamp!.Value))));
		var visitorSeries = buckets.SelectMany(b => new[] { "Neu in diesem Zeitraum", "Bereits zuvor im Zeitraum gesehen" }.Select((category, i) => new { bucketStart = b, category, count = (grouped.GetValueOrDefault(b) ?? []).Where(r => !string.IsNullOrWhiteSpace(r.VisitorId)).Select(r => r.VisitorId!).Distinct().Count(id => i == 0 ? firstVisitorBucket[id] == b : firstVisitorBucket[id] < b), partial = Partial(b) })).ToList();
		return new
		{
			range = range.Metadata,
			total = rows.Count,
			uniquePaths = paths.Count,
			topPaths = paths.Take(10).Select(p => new { path = p.Name, count = p.Value }),
			series = buckets.Select(b => { var values = grouped.GetValueOrDefault(b) ?? []; return new Point(b, values.Count, Partial(b), Sessions(values), Visitors(values), values.Select(InsightValues.Path).Distinct().Count(), PagesPerSession(values), values.Count(r => r.NavigationType == "reload")); }).ToList(),
			pathSeries = Segments(paths.Take(6).Select(p => p.Name).Append("Übrige"), r => paths.Take(6).Any(p => p.Name == InsightValues.Path(r)) ? InsightValues.Path(r) : "Übrige").Select(p => new { p.BucketStart, path = p.Name, p.Count, p.Partial }),
			devices = devices.Select(d => new { device = d.Name, count = d.Value }),
			deviceSeries = Segments(InsightValues.Devices, r => InsightValues.Device(r.ViewportWidth)).Select(p => new { p.BucketStart, device = p.Name, p.Count, p.Partial }),
			origins = origins.Select(o => new { origin = o.Name, count = o.Value }),
			originSeries = Segments(origins.Take(6).Select(o => o.Name).Append("Übrige"), r => InsightValues.Origin(r.ReferrerHost) is { } origin ? (origins.Take(6).Any(o => o.Name == origin) ? origin : "Übrige") : null).Select(p => new { p.BucketStart, origin = p.Name, p.Count, p.Partial }),
			sessions = Sessions(rows),
			withoutSessionId = rows.Count(r => string.IsNullOrWhiteSpace(r.SessionId)),
			pagesPerSession = PagesPerSession(rows),
			uniqueVisitors = Visitors(rows),
			reloads = rows.Count(r => r.NavigationType == "reload"),
			classifiedViews = rows.Count(InsightValues.Classified),
			visitorSeries,
		};
	}
}
