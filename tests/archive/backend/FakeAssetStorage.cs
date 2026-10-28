using System.Collections.Concurrent;
using Archive.Backend.Assets;

namespace Archive.Backend.Tests;

/// <summary>
/// In-memory replacement for the Blob storage adapter (ARC-015): objects live
/// in a dictionary, issued ticket URLs are opaque strings recorded with their
/// blob names so tests can emulate the browser transfer (PUT to uploadUrl)
/// without parsing tickets. Ticket URLs are returned verbatim by the API.
/// </summary>
internal sealed class FakeAssetStorage : IAssetStorageAdapter
{
	public sealed record IssuedTicket(string Url, string BlobName, TimeSpan Lifetime, bool Download, string? ContentType = null);

	private readonly ConcurrentDictionary<string, (byte[] Bytes, string? ContentType)> objects = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, string> tiers = new(StringComparer.Ordinal);
	private readonly List<IssuedTicket> tickets = [];
	private readonly object gate = new();
	private int sequence;

	public IReadOnlyList<IssuedTicket> Tickets
	{
		get
		{
			lock (gate) return [.. tickets];
		}
	}

	public int ObjectCount => objects.Count;

	public IssuedTicket? Find(string url) =>
		Tickets.FirstOrDefault(t => string.Equals(t.Url, url, StringComparison.Ordinal));

	/// <summary>
	/// Stores an object without a content type; ProbeAsync then reports null,
	/// which skips the endpoint's content-type check (pre-existing behavior).
	/// </summary>
	public void Store(string blobName, byte[] content) => objects[blobName] = (content, ContentType: null);

	/// <summary>Stores an object remembering the transfer content type.</summary>
	public void Store(string blobName, byte[] content, string contentType) =>
		objects[blobName] = (content, contentType);

	public bool Has(string blobName) => objects.ContainsKey(blobName);

	/// <summary>#148: the tier of an object; "hot" unless a tier change moved it.</summary>
	public string TierOf(string blobName) =>
		tiers.GetValueOrDefault(blobName, AssetStorageTiers.Hot);

	/// <summary>#148 outage seam: when set, tier changes fail with the generic storage message.</summary>
	public bool ThrowOnSetAccessTier { get; set; }

	public Task<string> CreateUploadTicketAsync(string blobName, TimeSpan lifetime, CancellationToken cancellationToken)
	{
		var url = NextUrl(blobName, "upload");
		lock (gate) tickets.Add(new IssuedTicket(url, blobName, lifetime, Download: false));
		return Task.FromResult(url);
	}

	public Task<string> CreateReadTicketAsync(string blobName, TimeSpan lifetime, bool asDownload, string? contentType = null, CancellationToken cancellationToken = default)
	{
		var url = NextUrl(blobName, asDownload ? "download" : "view");
		lock (gate) tickets.Add(new IssuedTicket(url, blobName, lifetime, asDownload, contentType));
		return Task.FromResult(url);
	}

	public Task<AssetObjectInfo?> ProbeAsync(string blobName, CancellationToken cancellationToken) =>
		Task.FromResult(objects.TryGetValue(blobName, out var stored)
			? new AssetObjectInfo(stored.Bytes.LongLength, stored.ContentType)
			: null);

	public Task<byte[]?> ReadHeaderAsync(string blobName, int length, CancellationToken cancellationToken) =>
		Task.FromResult<byte[]?>(objects.TryGetValue(blobName, out var stored)
			? stored.Bytes[..Math.Min(length, stored.Bytes.Length)]
			: null);

	/// <summary>ARC-034: a fresh read-only stream of the stored bytes; null when missing.</summary>
	public Task<Stream?> OpenReadAsync(string blobName, CancellationToken cancellationToken = default) =>
		Task.FromResult(objects.TryGetValue(blobName, out var stored)
			? (Stream?)new MemoryStream(stored.Bytes, writable: false)
			: null);

	public Task PromoteAsync(string sourceBlobName, string targetBlobName, CancellationToken cancellationToken)
	{
		if (!objects.TryGetValue(sourceBlobName, out var stored))
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
		// Mirror the Azure copy semantics: the staged object survives until
		// the endpoint deletes it after a successful promotion.
		objects[targetBlobName] = stored;
		return Task.CompletedTask;
	}

	public async IAsyncEnumerable<string> ListAsync(
		string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		foreach (var name in objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList())
			yield return name;
		await Task.CompletedTask;
	}

	public Task DeleteAsync(string blobName, CancellationToken cancellationToken)
	{
		objects.TryRemove(blobName, out _);
		return Task.CompletedTask;
	}

	/// <summary>#161: stores the copied object with its content type, unique-name rule.</summary>
	public async Task UploadAsync(string blobName, Stream content, string? contentType, CancellationToken cancellationToken)
	{
		using var reader = new MemoryStream();
		await content.CopyToAsync(reader, cancellationToken);
		if (!objects.TryAdd(blobName, (reader.ToArray(), contentType)))
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
		await Task.CompletedTask;
	}

	/// <summary>
	/// #148: records the tier like Azure would; a missing object is an error,
	/// mirroring the adapter's 404 translation.
	/// </summary>
	public Task SetAccessTierAsync(string blobName, string tier, CancellationToken cancellationToken)
	{
		if (ThrowOnSetAccessTier)
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
		if (!objects.ContainsKey(blobName))
			throw new InvalidOperationException("Speicherdienst nicht erreichbar.");
		tiers[blobName] = tier switch
		{
			AssetStorageTiers.Hot => AssetStorageTiers.Hot,
			AssetStorageTiers.Cold => AssetStorageTiers.Cold,
			_ => throw new InvalidOperationException("Die Speicher-Stufe ist unbekannt."),
		};
		return Task.CompletedTask;
	}

	private string NextUrl(string blobName, string kind)
	{
		var number = Interlocked.Increment(ref sequence);
		return $"sas://{blobName}?fake={kind}-{number}";
	}
}
