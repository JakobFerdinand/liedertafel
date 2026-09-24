using System.Diagnostics;
using Azure;
using Azure.Identity;
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
	/// <summary>
	/// Creates a time-bounded write SAS ticket for the browser upload.
	/// ARC-017: Read is included so the browser can query the pending blob's
	/// committed block list (<c>?comp=blocklist</c>) to resume interrupted
	/// block transfers.
	/// </summary>
	Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken);

	/// <summary>
	/// Creates a time-bounded read SAS ticket; <paramref name="asDownload"/>
	/// forces a download disposition. When <paramref name="contentType"/> is
	/// set, the ticket signs response-header overrides (content type plus
	/// inline/attachment disposition) that take precedence over the blob's
	/// stored properties: a block-list commit stores the transfer's
	/// application/xml default instead of the real file type, so without the
	/// override browsers cannot render the served file inline.
	/// </summary>
	Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default);

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

	/// <summary>
	/// ARC-049: hosted storage account endpoint (Entra-only, shared-key access
	/// disabled). When set, tickets are signed as user-delegation SAS through
	/// the runtime managed identity; otherwise the Azurite/local connection
	/// string is required.
	/// </summary>
	public string? ServiceUri { get; set; }

	public long MaxUploadBytes { get; set; } = 10 * 1024L * 1024 * 1024;

	/// <summary>ARC-017: recommended client block size for block uploads.</summary>
	public long UploadBlockBytes { get; set; } = 8 * 1024 * 1024;

	/// <summary>ARC-017: per musical version budget across pending sessions and finalized revisions.</summary>
	public long MaxCollectionBytes { get; set; } = 40 * 1024L * 1024 * 1024;

	public TimeSpan UploadSessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

	/// <summary>ARC-017: grace beyond ticket expiry before cleanup abandons a session.</summary>
	public TimeSpan ExpiryGrace { get; set; } = TimeSpan.FromHours(1);

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
	private BlobServiceClient? credentialClient;
	private UserDelegationKey? delegationKey;
	private readonly SemaphoreSlim delegationLock = new(1, 1);

	private BlobServiceClient Client()
	{
		var connectionString = configuration.GetConnectionString("archive-blobs");
		if (connectionString is { Length: > 0 })
			return new BlobServiceClient(connectionString, new BlobClientOptions
			{
				Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) },
			});
		// ARC-049: the hosted storage account is Entra-only (shared-key access
		// off), so tickets are signed as user-delegation SAS through the
		// runtime managed identity. AZURE_CLIENT_ID pins DefaultAzureCredential
		// to the user-assigned identity, the same pattern as mail and the
		// Data Protection key ring.
		var serviceUri = options.Value.ServiceUri;
		if (string.IsNullOrWhiteSpace(serviceUri))
			throw new InvalidOperationException("Kein Blob-Verbindungsstring konfiguriert.");
		credentialClient ??= new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential(),
			new BlobClientOptions
			{
				Retry = { MaxRetries = 2, NetworkTimeout = TimeSpan.FromSeconds(5) },
			});
		return credentialClient;
	}

	private BlobClient Blob(string blobName) =>
		Client().GetBlobContainerClient(options.Value.ContainerName).GetBlobClient(blobName);

	public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken) =>
		ExecuteAsync("upload_ticket", () => TicketUriAsync(blobName,
			BuildBuilder(blobName, lifetime,
				BlobSasPermissions.Write | BlobSasPermissions.Create | BlobSasPermissions.Read), cancellationToken));

	public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default) =>
		ExecuteAsync("read_ticket", async () =>
		{
			var builder = BuildBuilder(blobName, lifetime, BlobSasPermissions.Read);
			// Nur mit explizitem Typ signiert: dann besiegt der Ticket-Header
			// den gespeicherten (beim Block-Commit fälschlich application/xml)
			// Blobinhaltstyp — inline zum Ansehen, attachment zum Laden.
			if (contentType is { Length: > 0 })
			{
				builder.ContentType = contentType;
				builder.ContentDisposition = asDownload ? "attachment" : "inline";
			}
			return await TicketUriAsync(blobName, builder, cancellationToken);
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
				sourceBlobName, TimeSpan.FromSeconds(60), asDownload: false, cancellationToken: cancellationToken);
			await target.StartCopyFromUriAsync(new Uri(sourceTicket), cancellationToken: cancellationToken);
			var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(30);
			while (time.GetUtcNow() < deadline)
			{
				try
				{
					var properties = await target.GetPropertiesAsync(cancellationToken: cancellationToken);
					if (properties.Value.CopyStatus == CopyStatus.Success) return null;
					if (properties.Value.CopyStatus is CopyStatus.Aborted or CopyStatus.Failed)
						throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
				}
				catch (RequestFailedException exception) when (exception.Status == 404)
				{
					// Die Kopie ist noch nicht gelandet; innerhalb der Frist
					// weiter warten, statt den Poll als Ausfall zu werten.
				}
				await Task.Delay(250, cancellationToken);
			}
			throw new TimeoutException("Speicherdienst-Kopie wurde nicht rechtzeitig abgeschlossen.");
		});
	}

	public Task DeleteAsync(string blobName, CancellationToken cancellationToken) =>
		ExecuteAsync("delete", async () =>
			await Blob(blobName).DeleteIfExistsAsync(cancellationToken: cancellationToken));

	/// <summary>
	/// ARC-049: signs the ticket through user delegation in Entra mode and
	/// through the shared connection-string key against Azurite/local. The
	/// delegation key is cached and reused while it covers the requested
	/// window, so ordinary ticket traffic costs no extra control-plane call.
	/// </summary>
	private async Task<string> TicketUriAsync(
		string blobName, BlobSasBuilder builder, CancellationToken cancellationToken)
	{
		var client = Client();
		if (credentialClient is null)
			return Blob(blobName).GenerateSasUri(builder).ToString();
		var delegationKey = await DelegationKeyAsync(client, builder.ExpiresOn, cancellationToken);
		var sas = builder.ToSasQueryParameters(delegationKey, client.AccountName);
		return new UriBuilder(Blob(blobName).Uri) { Query = sas.ToString() }.Uri.ToString();
	}

	private async Task<UserDelegationKey> DelegationKeyAsync(
		BlobServiceClient client, DateTimeOffset expiresOn, CancellationToken cancellationToken)
	{
		await delegationLock.WaitAsync(cancellationToken);
		try
		{
			var now = time.GetUtcNow();
			if (delegationKey is not null
				&& delegationKey.SignedStartsOn <= now && delegationKey.SignedExpiresOn >= expiresOn)
				return delegationKey;
			delegationKey = (await client.GetUserDelegationKeyAsync(
				now - TimeSpan.FromMinutes(5), expiresOn, cancellationToken)).Value;
			return delegationKey;
		}
		finally
		{
			delegationLock.Release();
		}
	}

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
		catch (Exception exception) when (
			exception is RequestFailedException or CredentialUnavailableException)
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
