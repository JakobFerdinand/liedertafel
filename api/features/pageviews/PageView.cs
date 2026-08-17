using System.Net;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Liedertafel.Api.Shared.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Liedertafel.Api.Features.Pageviews;

public class PageView(PageView.Handler handler)
{
	[Function("pageview")]
	public async Task<HttpResponseData> Run(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData request,
		CancellationToken cancellationToken)
	{
		Payload? payload;
		try
		{
			payload = await JsonSerializer.DeserializeAsync<Payload>(request.Body, cancellationToken: cancellationToken);
		}
		catch (JsonException)
		{
			return await BadRequestAsync(request, "Invalid JSON payload.");
		}

		if (payload is null)
		{
			return await BadRequestAsync(request, "Invalid JSON payload.");
		}

		var error = handler.Validate(payload);
		if (error is not null)
		{
			return await BadRequestAsync(request, error);
		}

		await handler.SaveAsync(payload, cancellationToken);
		return request.CreateResponse(HttpStatusCode.NoContent);
	}

	private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData request, string message)
	{
		var response = request.CreateResponse(HttpStatusCode.BadRequest);
		await response.WriteStringAsync(message);
		return response;
	}

	public sealed record Payload(string? Path, string? ReferrerHost, int? ViewportWidth);

	public sealed class Handler(IPageViewWriteStore store)
	{
		private const int MaxPathLength = 200;
		private const int MaxReferrerHostLength = 200;
		private const int MaxViewportWidth = 10000;

		public string? Validate(Payload payload)
		{
			if (string.IsNullOrWhiteSpace(payload.Path) || !payload.Path.StartsWith('/'))
			{
				return "Field 'path' is required and must start with '/'.";
			}

			if (payload.Path.Length > MaxPathLength)
			{
				return $"Field 'path' must not exceed {MaxPathLength} characters.";
			}

			if (payload.ReferrerHost is { Length: > MaxReferrerHostLength })
			{
				return $"Field 'referrerHost' must not exceed {MaxReferrerHostLength} characters.";
			}

			if (payload.ViewportWidth is < 0 or > MaxViewportWidth)
			{
				return $"Field 'viewportWidth' must be between 0 and {MaxViewportWidth}.";
			}

			return null;
		}

		public async Task SaveAsync(Payload payload, CancellationToken ct)
		{
			var entity = new PageViewEntity
			{
				PartitionKey = $"Pv|{DateTime.UtcNow:yyyy-MM-dd}",
				RowKey = Guid.NewGuid().ToString(),
				Path = payload.Path!,
				ReferrerHost = payload.ReferrerHost,
				ViewportWidth = payload.ViewportWidth ?? 0,
			};
			await store.SaveAsync(entity, ct);
		}
	}

	public interface IPageViewWriteStore
	{
		Task SaveAsync(PageViewEntity entity, CancellationToken ct);
	}

	public sealed class TablePageViewStore(TableServiceClient client, ILogger<TablePageViewStore> logger) : IPageViewWriteStore
	{
		private const int RetentionMonths = 36;
		private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

		public async Task SaveAsync(PageViewEntity entity, CancellationToken ct)
		{
			var table = client.GetTableClient("pageviews");
			await table.CreateIfNotExistsAsync(ct);
			await table.AddEntityAsync(entity, ct);
			await TryCleanupAsync(table, ct);
		}

		private async Task TryCleanupAsync(TableClient table, CancellationToken ct)
		{
			try
			{
				var cleanupDue = true;
				try
				{
					var marker = await table.GetEntityAsync<TableEntity>("Cleanup", "last", cancellationToken: ct);
					if (marker.Value.Timestamp is { } ts && DateTimeOffset.UtcNow - ts < CleanupInterval)
					{
						cleanupDue = false;
					}
				}
				catch (RequestFailedException ex) when (ex.Status == 404)
				{
					cleanupDue = true;
				}

				if (!cleanupDue)
				{
					return;
				}

				await table.UpsertEntityAsync(new TableEntity("Cleanup", "last"), TableUpdateMode.Replace, ct);

				var cutoffKey = $"Pv|{DateTime.UtcNow.Date.AddMonths(-RetentionMonths):yyyy-MM-dd}";
				var filter = $"PartitionKey ge 'Pv|' and PartitionKey lt '{cutoffKey}'";

				while (true)
				{
					var deleted = 0;
					await foreach (var page in table.QueryAsync<TableEntity>(filter, maxPerPage: 1000).AsPages())
					{
						foreach (var group in page.Values.GroupBy(e => e.PartitionKey))
						{
							foreach (var chunk in group.Chunk(100))
							{
								await table.SubmitTransactionAsync(
									chunk.Select(e => new TableTransactionAction(TableTransactionActionType.Delete, e)).ToList(),
									ct);
								deleted += chunk.Length;
							}
						}
					}

					if (deleted == 0)
					{
						break;
					}
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Pageview retention cleanup failed; tracking continues.");
			}
		}
	}
}