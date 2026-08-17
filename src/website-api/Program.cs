using Azure.Data.Tables;
using Liedertafel.Api.Features.Pageviews;
using Liedertafel.Api.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
	.ConfigureFunctionsWorkerDefaults()
	.ConfigureServices(services =>
	{
		var connection = Environment.GetEnvironmentVariable(EnvironmentVariables.StorageConnection);
		if (string.IsNullOrWhiteSpace(connection))
		{
			throw new InvalidOperationException(
				$"Environment variable '{EnvironmentVariables.StorageConnection}' is not set. " +
				"Configure it as an Azure Static Web App app setting or in local.settings.json.");
		}

		services.AddSingleton(new TableServiceClient(connection));
		services.AddScoped<PageView.Handler>();
		services.AddScoped<PageView.IPageViewWriteStore, PageView.TablePageViewStore>();
	})
	.Build();

host.Run();
