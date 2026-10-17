using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// #172: the blobs of the dropped asset/revision model are released by a
/// finite operator command. It deletes everything under <c>revisions/</c> and
/// the staged <c>pending/</c> objects no upload session refers to any more;
/// <c>dateien/</c> (the Dateien of the flat model) and anything else is never
/// touched. A dry run only counts.
/// </summary>
public sealed class DroppedBlobCleanerTests
{
	private const string OldRevision = "revisions/9d4c/1";
	private const string OldRevisionTwo = "revisions/1a2b/7";
	private const string OrphanPending = "pending/orphan";
	private const string LivePending = "pending/live";
	private const string KeptDatei = "dateien/0f0f/abc";
	private const string Elsewhere = "other/file";

	[Fact]
	public async Task RevisionsAndOrphanedStagingAreDeletedAndDateienStay()
	{
		var (db, storage) = await SeedAsync();

		var result = await Cleaner(db, storage).CleanAsync(dryRun: false, CancellationToken.None);

		Assert.Equal(new DroppedBlobCleaner.Result(Revisions: 2, Staged: 1, Kept: 2, DryRun: false), result);
		Assert.False(storage.Has(OldRevision));
		Assert.False(storage.Has(OldRevisionTwo));
		Assert.False(storage.Has(OrphanPending));
		Assert.True(storage.Has(LivePending));
		Assert.True(storage.Has(KeptDatei));
		Assert.True(storage.Has(Elsewhere));
	}

	[Fact]
	public async Task ADryRunReportsTheSameCountsAndDeletesNothing()
	{
		var (db, storage) = await SeedAsync();

		var result = await Cleaner(db, storage).CleanAsync(dryRun: true, CancellationToken.None);

		Assert.Equal(new DroppedBlobCleaner.Result(Revisions: 2, Staged: 1, Kept: 2, DryRun: true), result);
		Assert.Equal(6, storage.ObjectCount);
	}

	[Fact]
	public async Task ASecondRunFindsNothingLeftToDelete()
	{
		var (db, storage) = await SeedAsync();
		await Cleaner(db, storage).CleanAsync(dryRun: false, CancellationToken.None);

		var second = await Cleaner(db, storage).CleanAsync(dryRun: false, CancellationToken.None);

		Assert.Equal(new DroppedBlobCleaner.Result(Revisions: 0, Staged: 0, Kept: 2, DryRun: false), second);
	}

	private static DroppedBlobCleaner Cleaner(ArchiveDbContext db, FakeAssetStorage storage) =>
		new(db, storage, NullLogger<DroppedBlobCleaner>.Instance);

	private static async Task<(ArchiveDbContext Db, FakeAssetStorage Storage)> SeedAsync()
	{
		var identity = new Microsoft.Extensions.Options.OptionsWrapper<IdentityOptions>(
			new IdentityOptions { Stores = { SchemaVersion = IdentitySchemaVersions.Version3 } });
		var provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
			.AddSingleton<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(identity).BuildServiceProvider();
		var db = new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
			.UseInMemoryDatabase($"dropped-blobs-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
			.UseApplicationServiceProvider(provider).Options);
		var account = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		var lied = new Lied { Title = "Lied", CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = now, UpdatedAt = now, RowVersion = 1 };
		db.Lieder.Add(lied);
		db.UploadSessions.Add(new PendingUpload
		{
			LiedId = lied.Id, DateiKind = DateiKinds.Noten, BlobName = LivePending, ContentType = "application/pdf",
			MaxSizeBytes = 10, UploadTicketExpiresAt = now.AddHours(1), State = PendingUploadState.Pending,
			CreatedByAccountId = account, CreatedAt = now,
		});
		await db.SaveChangesAsync();
		var storage = new FakeAssetStorage();
		foreach (var name in new[] { OldRevision, OldRevisionTwo, OrphanPending, LivePending, KeptDatei, Elsewhere })
			storage.Store(name, [1, 2, 3]);
		return (db, storage);
	}
}
