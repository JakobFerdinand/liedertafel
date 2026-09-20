using Archive.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Assets;

/// <summary>
/// ARC-017 bounded maintenance policy for abandoned upload sessions: pending
/// sessions whose upload ticket expired beyond the configured grace window
/// are marked abandoned and their pending blobs are deleted best-effort. The
/// cleaner is DI-injectable so the finite <c>--cleanup-uploads</c> job and
/// tests share one implementation; it never touches Finalized, Abandoned or
/// Cancelled sessions.
/// </summary>
public sealed class UploadSessionCleaner(
	ArchiveDbContext db, IAssetStorageAdapter storage, TimeProvider time,
	IOptions<AssetStorageOptions> options, ILogger<UploadSessionCleaner> logger)
{
	public async Task<int> CleanAsync(CancellationToken cancellationToken)
	{
		var cutoff = time.GetUtcNow() - options.Value.ExpiryGrace;
		var expired = await db.UploadSessions
			.Where(s => s.State == PendingUploadState.Pending
				&& s.UploadTicketExpiresAt < cutoff)
			.ToListAsync(cancellationToken);
		foreach (var session in expired)
		{
			session.State = PendingUploadState.Abandoned;
			try
			{
				await storage.DeleteAsync(session.BlobName, cancellationToken);
			}
			catch (InvalidOperationException exception)
			{
				// Best-effort: the pending blob may already be gone or the
				// provider briefly unavailable; the state transition stands.
				logger.LogWarning("Pending upload blob {BlobName} could not be deleted during cleanup",
					session.BlobName);
			}
		}
		if (expired.Count > 0)
			await db.SaveChangesAsync(cancellationToken);
		return expired.Count;
	}
}
