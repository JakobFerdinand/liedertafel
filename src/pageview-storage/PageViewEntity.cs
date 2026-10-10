using Azure;
using Azure.Data.Tables;

namespace PageViewStorage;

/// <summary>
/// One stored pageview. Property names are the Azure Table column names, so
/// renaming or retyping a property changes what both APIs read and write.
/// </summary>
public class PageViewEntity : ITableEntity
{
	public string Path { get; set; } = string.Empty;

	public string? ReferrerHost { get; set; }

	public int ViewportWidth { get; set; }

	public string? SessionId { get; set; }

	public string? VisitorId { get; set; }

	public string? NavigationType { get; set; }

	public string PartitionKey { get; set; } = string.Empty;

	public string RowKey { get; set; } = string.Empty;

	public DateTimeOffset? Timestamp { get; set; }

	public ETag ETag { get; set; }

	/// <summary>Builds the row the writer stores for a view observed at <paramref name="observedAt"/>.</summary>
	public static PageViewEntity Create(
		DateTimeOffset observedAt,
		string path,
		string? referrerHost,
		int viewportWidth,
		string? sessionId,
		string? visitorId,
		string? navigationType) => new()
	{
		PartitionKey = PageViewTable.PartitionKey(observedAt),
		RowKey = Guid.NewGuid().ToString(),
		Path = path,
		ReferrerHost = referrerHost,
		ViewportWidth = viewportWidth,
		SessionId = sessionId,
		VisitorId = visitorId,
		NavigationType = navigationType,
	};

	/// <summary>Whether the service-assigned timestamp falls in [<paramref name="utcStart"/>, <paramref name="utcEnd"/>).</summary>
	public bool IsWithin(DateTimeOffset utcStart, DateTimeOffset utcEnd) => Timestamp >= utcStart && Timestamp < utcEnd;
}
