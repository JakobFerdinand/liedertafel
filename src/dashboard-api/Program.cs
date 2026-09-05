using Azure.Data.Tables;
using DashboardApi.Features.PageViews;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
	.ConfigureFunctionsWorkerDefaults()
	.ConfigureServices(services =>
	{
		var connection = Environment.GetEnvironmentVariable("StorageConnection");
		if (string.IsNullOrWhiteSpace(connection))
		{
			throw new InvalidOperationException(
				"Environment variable 'StorageConnection' is not set. " +
				"Configure it as an Azure Static Web App app setting or in local.settings.json.");
		}

		services.AddSingleton(new TableServiceClient(connection));
		services.AddScoped<IInsightReader, TableInsightReader>();
		services.AddSingleton(new SessionHandles(connection));
		services.AddSingleton<SessionSnapshots>();
	})
	.Build();

host.Run();
