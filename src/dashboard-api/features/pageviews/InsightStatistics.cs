using PageViewStorage;

namespace DashboardApi.Features.PageViews;

public enum Granularity { Day, Week }

/// <summary>
/// The statistics of one Insight range, serialised as-is by the stats endpoint.
/// Property names are the wire contract mirrored by <c>src/dashboard/src/lib/api-client.ts</c>.
/// </summary>
public sealed record PageViewStatistics(
	RangeMetadata Range,
	int Total,
	int UniquePaths,
	IReadOnlyList<PathCount> TopPaths,
	IReadOnlyList<SeriesPoint> Series,
	IReadOnlyList<PathPoint> PathSeries,
	IReadOnlyList<DeviceCount> Devices,
	IReadOnlyList<DevicePoint> DeviceSeries,
	IReadOnlyList<OriginCount> Origins,
	IReadOnlyList<OriginPoint> OriginSeries,
	int Sessions,
	int WithoutSessionId,
	double PagesPerSession,
	int UniqueVisitors,
	int Reloads,
	int ClassifiedViews,
	IReadOnlyList<VisitorPoint> VisitorSeries);

public sealed record RangeMetadata(DateOnly Start, DateOnly End, string Timezone);
public sealed record PathCount(string Path, int Count);
public sealed record DeviceCount(string Device, int Count);
public sealed record OriginCount(string Origin, int Count);
public sealed record SeriesPoint(DateOnly BucketStart, int Count, bool Partial, int Sessions, int UniqueVisitors, int UniquePaths, double PagesPerSession, int Reloads);
public sealed record PathPoint(DateOnly BucketStart, string Path, int Count, bool Partial);
public sealed record DevicePoint(DateOnly BucketStart, string Device, int Count, bool Partial);
public sealed record OriginPoint(DateOnly BucketStart, string Origin, int Count, bool Partial);
public sealed record VisitorPoint(DateOnly BucketStart, string Category, int Count, bool Partial);

/// <summary>
/// Turns scanned page views into the statistics of one range. Every count the dashboard shows is defined here:
/// sessions and visitors are distinct non-blank ids, views without a session id are reported separately
/// and left out of pages per session, and long tails collapse into the top entries plus <see cref="Other"/>.
/// </summary>
public static class InsightStatistics
{
	public const string Other = "Übrige";
	public const string NewVisitors = "Neu in diesem Zeitraum";
	public const string ReturningVisitors = "Bereits zuvor im Zeitraum gesehen";
	private const int TopSegments = 6;
	private const int TopPathCount = 10;

	/// <summary>Rows outside <paramref name="range"/> are ignored, so callers may pass a wider scan.</summary>
	public static PageViewStatistics Compute(IReadOnlyList<PageViewEntity> scanned, InsightRange range, Granularity granularity)
	{
		var rows = new Views(scanned.Where(r => r.Timestamp >= range.UtcStart && r.Timestamp < range.UtcEnd).ToList());
		var timeline = new Timeline(range, granularity);
		var perBucket = rows.All.GroupBy(r => timeline.BucketOf(r)).ToDictionary(g => g.Key, g => new Views(g.ToList()));
		Views In(DateOnly bucket) => perBucket.GetValueOrDefault(bucket) ?? Views.Empty;

		var paths = Ranked(rows.All, InsightValues.Path);
		var origins = Ranked(rows.All, r => InsightValues.Origin(r.ReferrerHost));
		var pathBuckets = new TopBuckets(paths);
		var originBuckets = new TopBuckets(origins);

		IEnumerable<(DateOnly Bucket, string Name, int Count, bool Partial)> Segments(IReadOnlyList<string> names, Func<PageViewEntity, string?> key) =>
			timeline.Buckets.SelectMany(bucket =>
			{
				var counts = In(bucket).All.Select(key).OfType<string>().GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
				return names.Select(name => (bucket, name, counts.GetValueOrDefault(name), timeline.IsPartial(bucket)));
			});

		var firstBucket = rows.VisitorIds.ToDictionary(id => id, _ => DateOnly.MaxValue);
		foreach (var (bucket, views) in perBucket)
			foreach (var id in views.VisitorIds)
				if (bucket < firstBucket[id]) firstBucket[id] = bucket;

		return new PageViewStatistics(
			range.Metadata,
			rows.Count,
			paths.Count,
			paths.Take(TopPathCount).Select(p => new PathCount(p.Name, p.Value)).ToList(),
			timeline.Buckets.Select(b => new SeriesPoint(b, In(b).Count, timeline.IsPartial(b), In(b).Sessions, In(b).Visitors, In(b).UniquePaths, In(b).PagesPerSession, In(b).Reloads)).ToList(),
			Segments(pathBuckets.Names, r => pathBuckets.Assign(InsightValues.Path(r))).Select(p => new PathPoint(p.Bucket, p.Name, p.Count, p.Partial)).ToList(),
			InsightValues.Devices.Select(d => new DeviceCount(d, rows.All.Count(r => InsightValues.Device(r.ViewportWidth) == d))).ToList(),
			Segments(InsightValues.Devices, r => InsightValues.Device(r.ViewportWidth)).Select(p => new DevicePoint(p.Bucket, p.Name, p.Count, p.Partial)).ToList(),
			origins.Take(TopSegments).Select(o => new OriginCount(o.Name, o.Value)).ToList(),
			Segments(originBuckets.Names, r => originBuckets.Assign(InsightValues.Origin(r.ReferrerHost))).Select(p => new OriginPoint(p.Bucket, p.Name, p.Count, p.Partial)).ToList(),
			rows.Sessions,
			rows.WithoutSessionId,
			rows.PagesPerSession,
			rows.Visitors,
			rows.Reloads,
			rows.All.Count(InsightValues.Classified),
			timeline.Buckets.SelectMany(b => new[] { NewVisitors, ReturningVisitors }.Select((category, i) =>
				new VisitorPoint(b, category, In(b).VisitorIds.Count(id => i == 0 ? firstBucket[id] == b : firstBucket[id] < b), timeline.IsPartial(b)))).ToList());
	}

	private static List<(string Name, int Value)> Ranked(IEnumerable<PageViewEntity> rows, Func<PageViewEntity, string?> key) =>
		rows.Select(key).OfType<string>().GroupBy(k => k).Select(g => (Name: g.Key, Value: g.Count()))
			.OrderByDescending(c => c.Value).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();

	/// <summary>The counting rules for a set of views; the one place that decides what is a session, a visitor or a page per session.</summary>
	private sealed class Views(IReadOnlyList<PageViewEntity> all)
	{
		public static readonly Views Empty = new([]);
		public IReadOnlyList<PageViewEntity> All { get; } = all;
		public int Count => All.Count;
		public IReadOnlyList<string> SessionIds { get; } = Distinct(all.Select(r => r.SessionId));
		public IReadOnlyList<string> VisitorIds { get; } = Distinct(all.Select(r => r.VisitorId));
		public int Sessions => SessionIds.Count;
		public int Visitors => VisitorIds.Count;
		public int WithoutSessionId => All.Count(r => string.IsNullOrWhiteSpace(r.SessionId));
		public int UniquePaths => All.Select(InsightValues.Path).Distinct().Count();
		public int Reloads => All.Count(r => r.NavigationType == "reload");
		public double PagesPerSession => Sessions > 0 ? Math.Round((double)(Count - WithoutSessionId) / Sessions, 1) : 0;
		private static List<string> Distinct(IEnumerable<string?> ids) => ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList();
	}

	/// <summary>Monday-based week or single-day buckets over a range, and which of them only partly belong to it.</summary>
	private sealed class Timeline
	{
		private readonly InsightRange range;
		private readonly Granularity granularity;
		public Timeline(InsightRange range, Granularity granularity)
		{
			this.range = range;
			this.granularity = granularity;
			var step = granularity == Granularity.Day ? 1 : 7;
			for (var date = Bucket(range.Start); date <= range.End; date = date.AddDays(step)) Buckets.Add(date);
		}
		public List<DateOnly> Buckets { get; } = [];
		public DateOnly BucketOf(PageViewEntity row) => Bucket(InsightRange.LocalDate(row.Timestamp!.Value));
		public bool IsPartial(DateOnly bucket) => bucket < range.Start || bucket.AddDays(granularity == Granularity.Day ? 0 : 6) > range.End || (range.End == InsightRange.Today && bucket == Bucket(range.End));
		private DateOnly Bucket(DateOnly date) => granularity == Granularity.Day ? date : date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
	}

	/// <summary>The top entries of a ranking plus a trailing <see cref="Other"/> bucket for everything else.</summary>
	private sealed class TopBuckets
	{
		private readonly HashSet<string> top;
		public TopBuckets(IReadOnlyList<(string Name, int Value)> ranked)
		{
			var names = ranked.Take(TopSegments).Select(c => c.Name).ToList();
			top = [.. names];
			Names = [.. names, Other];
		}
		public IReadOnlyList<string> Names { get; }
		/// <summary>Null stays null (the value is not part of the ranking at all).</summary>
		public string? Assign(string? name) => name is null ? null : top.Contains(name) ? name : Other;
	}
}
