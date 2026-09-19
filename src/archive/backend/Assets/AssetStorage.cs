using System.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Assets;

public sealed record AssetObjectInfo(long SizeBytes, string? ContentType);

/// <summary>
/// Provider-backed storage/ticket adapter (architecture.md §Media transfer):
/// blob-scoped, time-bounded SAS tickets are the only file-access path.
/// Implementers must never expose storage credentials and must never log
/// ticket URLs. Production replaces shared-key ticket generation with
/// user-delegation SAS via managed identity without storage keys (ARC-049).
/// </summary>
public interface IAssetStorageAdapter
{
	/// <summary>Creates a time-bounded write SAS ticket for the browser upload.</summary>
	Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken);

	/// <summary>Creates a time-bounded read SAS ticket; <paramref name="asDownload"/> forces a download disposition.</summary>
	Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, CancellationToken cancellationToken);

	/// <summary>Returns null when the object does not exist.</summary>
	Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken);

	/// <summary>Reads at most <paramref name="length"/> leading bytes; null when the object does not exist.</summary>
	Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken);

	/// <summary>Server-side copy from a staged object to its final revision name.</summary>
	Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken);

	Task DeleteAsync(string blobName, CancellationToken cancellationToken);
}

public sealed class AssetStorageOptions
{
	public string ContainerName { get; set; } = "archive-assets";

	public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

	public TimeSpan UploadSessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

	public TimeSpan ReadTicketLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Azurite/development implementation over the <c>archive-blobs</c> connection
/// string. Production will use user-delegation SAS via managed identity
/// without storage keys (ARC-049); until then this adapter requires the local
/// connection string and throws otherwise. Exceptions are translated to a
/// generic German failure without provider details, and ticket URLs are never
/// logged.
/// </summary>
public sealed class BlobAssetStorageAdapter(
	IConfiguration configuration, IOptions<AssetStorageOptions> options, TimeProvider time) : IAssetStorageAdapter
{
	private BlobServiceClient Client()
	{
		var connectionString = configuration.GetConnectionString("archive-blobs")
			?? throw new InvalidOperationException("Kein Blob-Verbindungsstring konfiguriert.");
		return new BlobServiceClient(connectionString, new BlobClientOptions
		{
			Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) },
		});
	}

	private BlobClient Blob(string blobName) =>
		Client().GetBlobContainerClient(options.Value.ContainerName).GetBlobClient(blobName);

	public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken) =>
		ExecuteAsync("upload_ticket", () => Task.FromResult(
			Blob(blobName).GenerateSasUri(BuildBuilder(blobName, lifetime,
				BlobSasPermissions.Write | BlobSasPermissions.Create)).ToString()));

	public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, CancellationToken cancellationToken) =>
		ExecuteAsync("read_ticket", () =>
		{
			var builder = BuildBuilder(blobName, lifetime, BlobSasPermissions.Read);
			if (asDownload) builder.ContentDisposition = "attachment";
			return Task.FromResult(Blob(blobName).GenerateSasUri(builder).ToString());
		});

	public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken) =>
		ExecuteAsync<AssetObjectInfo?>("probe", async () =>
		{
			try
			{
				var properties = await Blob(blobName).GetPropertiesAsync(cancellationToken: cancellationToken);
				return new AssetObjectInfo(properties.Value.ContentLength, properties.Value.ContentType);
			}
			catch (RequestFailedException exception) when (exception.Status == 404)
			{
				return null;
			}
		});

	public Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken) =>
		ExecuteAsync<byte[]?>("read_header", async () =>
		{
			try
			{
				await using var stream = await Blob(blobName)
					.OpenReadAsync(new BlobOpenReadOptions(false), cancellationToken);
				var header = new byte[length];
				var read = 0;
				while (read < header.Length)
				{
					var chunk = await stream.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
					if (chunk == 0) break;
					read += chunk;
				}
				return header[..read];
			}
			catch (RequestFailedException exception) when (exception.Status == 404)
			{
				return null;
			}
		});

	public async Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken)
	{
		await ExecuteAsync<object?>("promote", async () =>
		{
			var target = Blob(targetBlobName);
			var sourceTicket = await CreateReadTicketAsync(
				sourceBlobName, TimeSpan.FromSeconds(60), asDownload: false, cancellationToken);
			await target.StartCopyFromUriAsync(new Uri(sourceTicket), cancellationToken: cancellationToken);
			var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(30);
			while (time.GetUtcNow() < deadline)
			{
				var properties = await target.GetPropertiesAsync(cancellationToken: cancellationToken);
				if (properties.Value.CopyStatus == CopyStatus.Success) return null;
				if (properties.Value.CopyStatus is CopyStatus.Aborted or CopyStatus.Failed)
					throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
				await Task.Delay(250, cancellationToken);
			}
			throw new TimeoutException("Speicherdienst-Kopie wurde nicht rechtzeitig abgeschlossen.");
		});
	}

	public Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
		ExecuteAsync("delete", async () =>
			await Blob(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken));

	private BlobSasBuilder BuildBuilder(string blobName, TimeSpan lifetime, BlobSasPermissions permissions) =>
		new(permissions, time.GetUtcNow() + lifetime)
		{
			BlobContainerName = options.Value.ContainerName,
			BlobName = blobName,
		};

	private async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action)
	{
		using var activity = Extensions.Activities.StartActivity($"archive.assets.{operation}", ActivityKind.Client);
		try
		{
			return await action();
		}
		catch (RequestFailedException exception)
		{
			activity?.SetStatus(ActivityStatusCode.Error);
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.", exception);
		}
	}
}

/// <summary>
/// Emulator-only browser CORS for direct transfers: grants the local
/// frontend a permissive rule so direct-to-storage uploads work against
/// Azurite. Production CORS is configured in infrastructure (ARC-042/ARC-049).
/// </summary>
public static class AssetStorageEmulatorBootstrap
{
	public static async Task EnsureEmulatorCorsAsync(BlobServiceClient client, CancellationToken cancellationToken)
	{
		var properties = (await client.GetPropertiesAsync(cancellationToken)).Value;
		if (properties.Cors is { Count: > 0 }) return;
		properties.Cors = [new BlobCorsRule
		{
			AllowedOrigins = "*",
			AllowedMethods = "GET,HEAD,PUT,OPTIONS",
			AllowedHeaders = "*",
			ExposedHeaders = "*",
			MaxAgeInSeconds = 3600,
		}];
		await client.SetPropertiesAsync(properties, cancellationToken);
	}
}
