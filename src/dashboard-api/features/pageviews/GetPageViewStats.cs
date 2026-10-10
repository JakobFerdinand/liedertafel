using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

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
		var resolution = granularity == "day" ? Granularity.Day : Granularity.Week;
		var compare = request.Query["compare"] ?? "previous_period";
		if (compare is not ("previous_period" or "none")) throw new QueryException("Der Vergleich muss previous_period oder none sein.");
		var range = InsightRange.Parse(request.Query, resolution == Granularity.Day ? 92 : 400);
		if (compare == "previous_period" && range.Previous.Start < InsightRange.Today.AddMonths(-36))
			throw new QueryException("Die Vorperiode liegt außerhalb der letzten 36 Monate. Bitte den Vergleich ausschalten.");
		var scan = await reader.ReadAsync(compare == "previous_period" ? new(range.Previous.Start, range.End) : range, cancellationToken);
		return new
		{
			range = range.Metadata,
			granularity,
			generatedAt = DateTimeOffset.UtcNow,
			truncated = scan.Truncated,
			current = InsightStatistics.Compute(scan.Rows, range, resolution),
			previous = compare == "previous_period" ? InsightStatistics.Compute(scan.Rows, range.Previous, resolution) : null,
		};
	}, cancellationToken);
}
