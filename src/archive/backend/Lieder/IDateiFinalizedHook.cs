using Archive.Backend.Data;

namespace Archive.Backend.Lieder;

/// <summary>
/// Extension point for work that follows a finalized Datei upload, registered
/// in DI (<c>services.AddSingleton&lt;IDateiFinalizedHook, ...&gt;()</c>), so the
/// finalize body stays owned by <see cref="DateiUploads"/>. #169 enqueues text
/// extraction here, #168 sends the replaced Datei to the trash.
/// </summary>
public interface IDateiFinalizedHook
{
	/// <summary>
	/// Runs before the save that commits the new Datei, in the same
	/// transaction: add outbox rows or change related rows here.
	/// </summary>
	Task BeforeSaveAsync(Datei datei, PendingUploadContext upload, ArchiveDbContext db, CancellationToken token)
		=> Task.CompletedTask;

	/// <summary>Runs after the save; best effort, a failure never fails the upload.</summary>
	Task AfterSaveAsync(Datei datei, ArchiveDbContext db, CancellationToken token)
		=> Task.CompletedTask;
}

/// <summary>The upload-session facts a hook may need (#168 replaces, #171 owners).</summary>
public sealed record PendingUploadContext(Guid? ReplacesDateiId, Guid ActorAccountId, DateTimeOffset Now);
