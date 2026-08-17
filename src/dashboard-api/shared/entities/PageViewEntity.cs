using Azure;
using Azure.Data.Tables;

namespace DashboardApi.Shared.Entities;

public class PageViewEntity : ITableEntity
{
	public string Path { get; set; } = string.Empty;

	public string? ReferrerHost { get; set; }

	public int ViewportWidth { get; set; }

	public string PartitionKey { get; set; } = string.Empty;

	public string RowKey { get; set; } = string.Empty;

	public DateTimeOffset? Timestamp { get; set; }

	public ETag ETag { get; set; }
}
