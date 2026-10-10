using Azure.Data.Tables;

namespace PageViewStorage;

/// <summary>
/// Where pageviews live: the table, one partition per UTC day (<c>Pv|yyyy-MM-dd</c>)
/// and the filters that select partitions. The writer derives keys here and the
/// reader derives its scan bounds here, so the two cannot drift apart.
/// </summary>
public static class PageViewTable
{
	public const string Name = "pageviews";

	/// <summary>Key of the marker row the writer uses to throttle retention cleanup. Outside every <c>Pv|</c> partition.</summary>
	public const string CleanupPartitionKey = "Cleanup";

	public const string CleanupRowKey = "last";

	private const string PartitionPrefix = "Pv|";

	public static TableClient Client(TableServiceClient service) => service.GetTableClient(Name);

	public static string PartitionKey(DateTimeOffset observedAt) => PartitionKeyOfDay(observedAt.UtcDateTime);

	/// <summary>Filter for every pageview partition that can hold a row in [<paramref name="utcStart"/>, <paramref name="utcEnd"/>).</summary>
	public static string RangeFilter(DateTimeOffset utcStart, DateTimeOffset utcEnd) =>
		TableClient.CreateQueryFilter($"PartitionKey ge {PartitionKey(utcStart)} and PartitionKey le {PartitionKey(utcEnd.AddTicks(-1))}");

	/// <summary>Filter for every pageview partition older than <paramref name="months"/> months before <paramref name="now"/>.</summary>
	public static string RetentionFilter(DateTimeOffset now, int months) =>
		$"PartitionKey ge '{PartitionPrefix}' and PartitionKey lt '{PartitionKeyOfDay(now.UtcDateTime.Date.AddMonths(-months))}'";

	private static string PartitionKeyOfDay(DateTime utc) => $"{PartitionPrefix}{utc:yyyy-MM-dd}";
}
