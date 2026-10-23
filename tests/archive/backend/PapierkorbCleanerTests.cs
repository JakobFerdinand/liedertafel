using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Archive.Backend.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Backend.Tests.PapierkorbCleanerTestsNs;

/// <summary>
/// #146: the finite <c>--cleanup-papierkorb</c> command removes trashed Lieder,
/// Auftritte and Dateien past the seven-day retention — rows first, blobs
/// afterwards — and keeps everything deleted more recently. A dry run only
/// counts; a second run finds nothing left.
/// </summary>
public sealed class PapierkorbCleanerTests
{
	[Fact]
	public async Task ExpiredTrashIsRemovedAndRecentTrashStays()
	{
		var (db, storage, now) = await SeedAsync();

		// Dry run only counts.
		var dry = await Cleaner(db, storage).CleanAsync(dryRun: true, CancellationToken.None);
		Assert.Equal(new PapierkorbCleaner.Result(Lieder: 1, Auftritte: 1, Dateien: 1, RetentionDays: PapierkorbCleaner.DefaultRetentionDays, DryRun: true), dry);
		Assert.Equal(2, storage.ObjectCount);

		var real = await Cleaner(db, storage).CleanAsync(dryRun: false, CancellationToken.None);
		Assert.Equal(new PapierkorbCleaner.Result(Lieder: 1, Auftritte: 1, Dateien: 1, RetentionDays: PapierkorbCleaner.DefaultRetentionDays, DryRun: false), real);
		Assert.False(await db.Lieder.AnyAsync(l => l.Title == "Altes Lied"));
		Assert.False(await db.Events.AnyAsync(e => e.Title == "Alter Auftritt"));
		Assert.False(await db.Dateien.AnyAsync(d => d.OriginalFileName == "alt.pdf"));
		Assert.True(await db.Lieder.AnyAsync(l => l.Title == "Frisch"));
		Assert.True(await db.Dateien.AnyAsync(d => d.OriginalFileName == "frisch.pdf"));
		Assert.Equal(1, storage.ObjectCount);

		// A second run finds nothing left.
		var second = await Cleaner(db, storage).CleanAsync(dryRun: false, CancellationToken.None);
		Assert.Equal(new PapierkorbCleaner.Result(Lieder: 0, Auftritte: 0, Dateien: 0, RetentionDays: PapierkorbCleaner.DefaultRetentionDays, DryRun: false), second);
	}

	[Fact]
	public async Task TheRetentionCanBeSingledOutPerSetting()
	{
		var (db, storage, _) = await SeedAsync();
		var setting = new ConfigurationBuilder().AddInMemoryCollection(
			[new System.Collections.Generic.KeyValuePair<string, string?>(PapierkorbCleaner.RetentionDaysPath, "30")]).Build();
		var cleaner = new PapierkorbCleaner(db, Removal(db, storage), TimeProvider.System, setting,
			NullLogger<PapierkorbCleaner>.Instance);
		var result = await cleaner.CleanAsync(dryRun: true, CancellationToken.None);
		Assert.Equal(30, result.RetentionDays);
		Assert.Equal(0, result.Lieder);
	}

	private static PapierkorbCleaner Cleaner(ArchiveDbContext db, FakeAssetStorage storage)
	{
		var configuration = new ConfigurationBuilder().Build();
		return new(db, Removal(db, storage), TimeProvider.System, configuration, NullLogger<PapierkorbCleaner>.Instance);
	}

	private static PapierkorbRemoval Removal(ArchiveDbContext db, FakeAssetStorage storage) =>
		new(db, storage, NullLogger<PapierkorbRemoval>.Instance);

	private static async Task<(ArchiveDbContext Db, FakeAssetStorage Storage, DateTimeOffset Now)> SeedAsync()
	{
		var identity = new Microsoft.Extensions.Options.OptionsWrapper<Microsoft.AspNetCore.Identity.IdentityOptions>(
			new Microsoft.AspNetCore.Identity.IdentityOptions
			{
				Stores = { SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3 },
			});
		var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
		services.AddSingleton<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.IdentityOptions>>(identity);
		var provider = services.BuildServiceProvider();
		var db = new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
			.UseInMemoryDatabase($"papierkorb-cleaner-{Guid.NewGuid():N}", new InMemoryDatabaseRoot())
			.UseApplicationServiceProvider(provider).Options);
		var account = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		var oldLied = SeedLied("Altes Lied", account, now);
		var freshLied = SeedLied("Frisch", account, now);
		oldLied.DeletedAt = now.AddDays(-10);
		freshLied.DeletedAt = now.AddDays(-1);
		oldLied.DeletedByAccountId = account;
		freshLied.DeletedByAccountId = account;
		db.Lieder.AddRange(oldLied, freshLied);
		db.Dateien.AddRange(
			new Datei
			{
				LiedId = freshLied.Id, Kind = DateiKinds.Noten, OriginalFileName = "alt.pdf", ContentType = "application/pdf",
				BlobName = "dateien/a/old", SizeBytes = 12, CreatedByAccountId = account, CreatedAt = now, RowVersion = 1,
				DeletedAt = now.AddDays(-10), // individually expired
			},
			new Datei
			{
				LiedId = freshLied.Id, Kind = DateiKinds.Noten, OriginalFileName = "frisch.pdf", ContentType = "application/pdf",
				BlobName = "dateien/b/fresh", SizeBytes = 12, CreatedByAccountId = account, CreatedAt = now, RowVersion = 1,
			});
		var oldEvent = new ChoirEvent
		{
			Title = "Alter Auftritt", Kind = "concert",
			CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = now, UpdatedAt = now, RowVersion = 1,
			DeletedAt = now.AddDays(-10), DeletedByAccountId = account,
		};
		db.Events.Add(oldEvent);
		await db.SaveChangesAsync();
		var storage = new FakeAssetStorage();
		storage.Store("dateien/a/old", [1, 2, 3]);
		storage.Store("dateien/b/fresh", [4, 5, 6]);
		return (db, storage, now);
	}

	private static Lied SeedLied(string title, Guid account, DateTimeOffset now) =>
		new()
		{
			Title = title, CreatedByAccountId = account, UpdatedByAccountId = account,
			CreatedAt = now, UpdatedAt = now, RowVersion = 1,
		};
}
