using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using DashboardApi.Shared.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DashboardApi.Features.PageViews;

public class GetPageViewStats(GetPageViewStats.Handler handler)
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private static readonly int[] DayPresets = [28, 90, 180];

	[Function("get-pageview-stats")]
	public async Task<HttpResponseData> Run(
		[HttpTrigger(AuthorizationLevel.Function, "get", Route = "pageviews/stats")] HttpRequestData request,
		FunctionContext context,
		CancellationToken cancellationToken)
	{
		var days = ParseDays(request.Query["days"]);
		var stats = await handler.GetAsync(days, cancellationToken);

		var response = request.CreateResponse(HttpStatusCode.OK);
		response.Headers.Add("Content-Type", "application/json; charset=utf-8");
		await response.WriteStringAsync(JsonSerializer.Serialize(stats, JsonOptions));
		return response;
	}

	private static int ParseDays(string? raw)
	{
		if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) &&
			DayPresets.Contains(days))
		{
			return days;
		}

		return DayPresets[0];
	}

	public sealed record StatsResponse(
		int Total,
		int UniquePaths,
		IReadOnlyList<PathCount> TopPaths,
		IReadOnlyList<SeriesPoint> Series,
		IReadOnlyList<PathSeriesPoint> PathSeries,
		IReadOnlyList<DeviceCount> Devices,
		IReadOnlyList<DeviceSeriesPoint> DeviceSeries,
		IReadOnlyList<OriginCount> Origins,
		IReadOnlyList<OriginSeriesPoint> OriginSeries,
		int Sessions,
		double PagesPerSession,
		int UniqueVisitors,
		int Reloads,
		IReadOnlyList<VisitorSeriesPoint> VisitorSeries);

	public sealed record PathCount(string Path, int Count);

	public sealed record SeriesPoint(string Week, int Count);

	public sealed record PathSeriesPoint(string Week, string Path, int Count)
	{
		public static PathSeriesPoint Create(string week, string name, int count) => new(week, name, count);
	}

	public sealed record DeviceCount(string Device, int Count);

	public sealed record DeviceSeriesPoint(string Week, string Device, int Count)
	{
		public static DeviceSeriesPoint Create(string week, string name, int count) => new(week, name, count);
	}

	public sealed record OriginCount(string Origin, int Count);

	public sealed record OriginSeriesPoint(string Week, string Origin, int Count)
	{
		public static OriginSeriesPoint Create(string week, string name, int count) => new(week, name, count);
	}

	public sealed record VisitorSeriesPoint(string Week, string Category, int Count)
	{
		public static VisitorSeriesPoint Create(string week, string category, int count) => new(week, category, count);
	}

	public sealed class Handler(IStatsReader reader)
	{
		private const int MaxTopPaths = 10;
		private const int MaxPathSeries = 6;
		private const int MaxOrigins = 6;
		private const string OtherBucket = "Übrige";

		private static readonly string[] InternalHosts =
		[
			"liedertafel-mining.at",
			"www.liedertafel-mining.at",
			"dashboard.liedertafel-mining.at",
		];

		private const string AzureStaticAppsSuffix = ".azurestaticapps.net";

		public async Task<StatsResponse> GetAsync(int days, CancellationToken ct)
		{
			var today = DateTime.UtcNow.Date;
			var windowStart = today.AddDays(-(days - 1));

			var entities = await reader.ReadAsync(
				$"Pv|{windowStart:yyyy-MM-dd}",
				$"Pv|{today:yyyy-MM-dd}",
				ct);

			var weeks = BuildWeeks(WeekStart(windowStart), WeekStart(today));

			var seriesByWeek = new Dictionary<string, int>(StringComparer.Ordinal);
			var pathsByWeek = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
			var devicesByWeek = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
			var originsByWeek = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
			var pathTotals = new Dictionary<string, int>(StringComparer.Ordinal);
			var deviceTotals = new Dictionary<string, int>(StringComparer.Ordinal);
			var originTotals = new Dictionary<string, int>(StringComparer.Ordinal);
			var sessionIds = new HashSet<string>(StringComparer.Ordinal);
			var visitorIds = new HashSet<string>(StringComparer.Ordinal);
			var weeksByVisitor = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			var reloads = 0;
			var pageViewsWithSession = 0;

			foreach (var entity in entities)
			{
				var date = DateFromPartition(entity.PartitionKey) ?? entity.Timestamp?.UtcDateTime.Date ?? today;
				var week = FormatWeek(WeekStart(date));

				Increment(seriesByWeek, week);

				var path = string.IsNullOrWhiteSpace(entity.Path) ? "(unbekannt)" : entity.Path;
				Increment(GetBucket(pathsByWeek, week), path);
				Increment(pathTotals, path);

				var device = CategorizeDevice(entity.ViewportWidth);
				Increment(GetBucket(devicesByWeek, week), device);
				Increment(deviceTotals, device);

				var origin = NormalizeOrigin(entity.ReferrerHost);
				if (origin is not null)
				{
					Increment(GetBucket(originsByWeek, week), origin);
					Increment(originTotals, origin);
				}

				if (!string.IsNullOrWhiteSpace(entity.SessionId))
				{
					sessionIds.Add(entity.SessionId);
					pageViewsWithSession++;
				}

				if (!string.IsNullOrWhiteSpace(entity.VisitorId))
				{
					visitorIds.Add(entity.VisitorId);
					if (!weeksByVisitor.TryGetValue(entity.VisitorId, out var visitorWeeks))
					{
						visitorWeeks = new HashSet<string>(StringComparer.Ordinal);
						weeksByVisitor[entity.VisitorId] = visitorWeeks;
					}

					visitorWeeks.Add(week);
				}

				if (entity.NavigationType == "reload")
				{
					reloads++;
				}
			}

			var sessions = sessionIds.Count;
			var pagesPerSession = sessions > 0 ? Math.Round((double)pageViewsWithSession / sessions, 1) : 0;
			var uniqueVisitors = visitorIds.Count;

			var topPaths = pathTotals
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(MaxTopPaths)
				.Select(pair => new PathCount(pair.Key, pair.Value))
				.ToList();

			var topPathNames = pathTotals
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(MaxPathSeries)
				.Select(pair => pair.Key)
				.ToList();

			var topOrigins = originTotals
				.OrderByDescending(pair => pair.Value)
				.ThenBy(pair => pair.Key, StringComparer.Ordinal)
				.Take(MaxOrigins)
				.Select(pair => new OriginCount(pair.Key, pair.Value))
				.ToList();

			var topOriginNames = topOrigins.Select(origin => origin.Origin).ToHashSet(StringComparer.Ordinal);

			var deviceNames = deviceTotals.Count == 0
				? DeviceCategories
				: DeviceCategories.Where(deviceTotals.ContainsKey).ToList();

			var visitorsByWeek = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
			foreach (var pair in weeksByVisitor)
			{
				var firstWeek = pair.Value.OrderBy(week => week, StringComparer.Ordinal).First();
				Increment(GetBucket(visitorsByWeek, firstWeek), NewVisitorCategory);
				foreach (var week in pair.Value.Where(week => week != firstWeek))
				{
					Increment(GetBucket(visitorsByWeek, week), ReturningVisitorCategory);
				}
			}

			var series = weeks
				.Select(week => new SeriesPoint(week, seriesByWeek.GetValueOrDefault(week)))
				.ToList();

			var pathSeries = BuildSeries(weeks, pathsByWeek, topPathNames, includeOther: true, PathSeriesPoint.Create);
			var deviceSeries = BuildSeries(weeks, devicesByWeek, deviceNames, includeOther: false, DeviceSeriesPoint.Create);
			var originSeries = BuildSeries(weeks, originsByWeek, topOriginNames.ToList(), includeOther: true, OriginSeriesPoint.Create);
			var visitorSeries = BuildSeries(weeks, visitorsByWeek, VisitorCategories, includeOther: false, VisitorSeriesPoint.Create);

			var devices = deviceNames
				.Select(device => new DeviceCount(device, deviceTotals.GetValueOrDefault(device)))
				.ToList();

			var origins = topOrigins
				.Concat(GetOther(originTotals, topOriginNames))
				.ToList();

			return new StatsResponse(
				entities.Count,
				pathTotals.Count,
				topPaths,
				series,
				pathSeries,
				devices,
				deviceSeries,
				origins,
				originSeries,
				sessions,
				pagesPerSession,
				uniqueVisitors,
				reloads,
				visitorSeries);
		}

		private static IReadOnlyList<TSeries> BuildSeries<TSeries>(
			IReadOnlyList<string> weeks,
			IReadOnlyDictionary<string, Dictionary<string, int>> buckets,
			IReadOnlyList<string> names,
			bool includeOther,
			Func<string, string, int, TSeries> factory)
		{
			var points = new List<TSeries>();
			foreach (var week in weeks)
			{
				var bucket = buckets.GetValueOrDefault(week);
				foreach (var name in names)
				{
					points.Add(factory(week, name, bucket?.GetValueOrDefault(name) ?? 0));
				}

				if (includeOther)
				{
					var other = bucket is null
						? 0
						: bucket.Where(pair => !names.Contains(pair.Key)).Sum(pair => pair.Value);
					points.Add(factory(week, OtherBucket, other));
				}
			}

			return points;
		}

		private static IReadOnlyList<OriginCount> GetOther(
			IReadOnlyDictionary<string, int> totals,
			ISet<string> included)
		{
			var other = totals.Where(pair => !included.Contains(pair.Key)).Sum(pair => pair.Value);
			return other > 0 ? [new OriginCount(OtherBucket, other)] : [];
		}

		private static Dictionary<string, int> GetBucket(
			Dictionary<string, Dictionary<string, int>> buckets,
			string week)
		{
			if (!buckets.TryGetValue(week, out var bucket))
			{
				bucket = new Dictionary<string, int>(StringComparer.Ordinal);
				buckets[week] = bucket;
			}

			return bucket;
		}

		private static void Increment(Dictionary<string, int> counts, string key)
		{
			counts[key] = counts.GetValueOrDefault(key) + 1;
		}

		private static string CategorizeDevice(int width) => width switch
		{
			< 768 => "Mobil",
			< 1024 => "Tablet",
			< 1440 => "Laptop",
			_ => "Breitbild",
		};

		private static readonly IReadOnlyList<string> DeviceCategories = ["Mobil", "Tablet", "Laptop", "Breitbild"];

		private const string NewVisitorCategory = "Neu";

		private const string ReturningVisitorCategory = "Wiederkehrend";

		private static readonly IReadOnlyList<string> VisitorCategories = [NewVisitorCategory, ReturningVisitorCategory];

		private static string? NormalizeOrigin(string? host)
		{
			if (string.IsNullOrWhiteSpace(host))
			{
				return null;
			}

			var origin = host.Trim().ToLowerInvariant();
			if (InternalHosts.Contains(origin) ||
				origin.EndsWith(AzureStaticAppsSuffix, StringComparison.Ordinal))
			{
				return null;
			}

			return origin;
		}

		private static DateTime? DateFromPartition(string partitionKey)
		{
			if (!partitionKey.StartsWith("Pv|", StringComparison.Ordinal))
			{
				return null;
			}

			if (DateTime.TryParseExact(
				partitionKey.AsSpan(3),
				"yyyy-MM-dd",
				CultureInfo.InvariantCulture,
				DateTimeStyles.None,
				out var date))
			{
				return date;
			}

			return null;
		}

		private static DateTime WeekStart(DateTime date)
		{
			var day = (int)date.DayOfWeek;
			var diff = day == 0 ? -6 : 1 - day;
			return date.Date.AddDays(diff);
		}

		private static string FormatWeek(DateTime weekStart) =>
			weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		private static List<string> BuildWeeks(DateTime start, DateTime end)
		{
			var weeks = new List<string>();
			for (var week = start; week <= end; week = week.AddDays(7))
			{
				weeks.Add(FormatWeek(week));
			}

			return weeks;
		}
	}

	public interface IStatsReader
	{
		Task<IReadOnlyList<PageViewEntity>> ReadAsync(string startPartition, string endPartition, CancellationToken ct);
	}

	public sealed class TableStatsReader(TableServiceClient client, ILogger<TableStatsReader> logger) : IStatsReader
	{
		public async Task<IReadOnlyList<PageViewEntity>> ReadAsync(string startPartition, string endPartition, CancellationToken ct)
		{
			var table = client.GetTableClient("pageviews");
			var filter = $"PartitionKey ge '{startPartition}' and PartitionKey le '{endPartition}'";

			var entities = new List<PageViewEntity>();
			try
			{
				await foreach (var page in table.QueryAsync<PageViewEntity>(filter, maxPerPage: 1000).AsPages())
				{
					foreach (var entity in page.Values)
					{
						entities.Add(entity);
					}
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to read pageview statistics from table storage.");
				throw;
			}

			return entities;
		}
	}
}
