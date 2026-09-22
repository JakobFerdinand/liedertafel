using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Archive.Backend.Data;

// Identity owns users/roles/sessions; each feature slice owns its own tables
// (challenges and rate-limit evidence live here, catalogue slices add theirs).
public sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options)
	: IdentityDbContext<ArchiveUser, ArchiveRole, Guid>(options)
{
	// ARC-011-1: Identity schema version 3 (passkey tables). The version is
	// carried by IdentityOptions.Stores.SchemaVersion (set in AddArchiveIdentity)
	// and reaches the model through the EF application service provider.
	// Design-time builds without a service provider are covered by
	// ArchiveDbContextFactory below.

	public DbSet<SignInChallenge> SignInChallenges => Set<SignInChallenge>();

	public DbSet<AuthRequestLog> AuthRequestLogs => Set<AuthRequestLog>();

	public DbSet<MemberInvitation> MemberInvitations => Set<MemberInvitation>();

	public DbSet<MemberAdminAction> MemberAdminActions => Set<MemberAdminAction>();

	public DbSet<MemberEmailChange> MemberEmailChanges => Set<MemberEmailChange>();

	public DbSet<Song> Songs => Set<Song>();

	public DbSet<Arrangement> Arrangements => Set<Arrangement>();

	public DbSet<MusicalVersion> MusicalVersions => Set<MusicalVersion>();

	public DbSet<SongTitle> SongTitles => Set<SongTitle>();

	public DbSet<ArchiveAsset> Assets => Set<ArchiveAsset>();

	public DbSet<FileRevision> FileRevisions => Set<FileRevision>();

	public DbSet<PendingUpload> UploadSessions => Set<PendingUpload>();

	public DbSet<Chat.ChatThread> ChatThreads => Set<Chat.ChatThread>();

	public DbSet<Chat.ChatMessage> ChatMessages => Set<Chat.ChatMessage>();

	public DbSet<Chat.ChatUsageEntry> ChatUsageEntries => Set<Chat.ChatUsageEntry>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArchiveUser).Assembly);
	}
}

public sealed class ArchiveDbContextFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        // Design-time builds have no application service provider, so the
        // Identity schema version would silently fall back to Version 1 and
        // migrations would miss the passkey tables. Supply the same version
        // the runtime registers (ARC-011-1).
        var identityOptions = new OptionsWrapper<IdentityOptions>(new IdentityOptions
        {
            Stores = { SchemaVersion = IdentitySchemaVersions.Version3 },
        });
        var applicationServices = new ServiceCollection()
            .AddSingleton<IOptions<IdentityOptions>>(identityOptions)
            .BuildServiceProvider();
        return new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(DatabaseConfiguration.Connection(configuration, "archive-migrations"))
            .UseApplicationServiceProvider(applicationServices).Options);
    }
}

public static class DatabaseConfiguration
{
    public static string Connection(IConfiguration configuration, string name)
    {
        var value = configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"ConnectionStrings:{name} is required for this operation.");
        return new NpgsqlConnectionStringBuilder(value)
        {
            MaxPoolSize = 5, MinPoolSize = 0, Timeout = 5, CommandTimeout = 10,
            KeepAlive = 0, IncludeErrorDetail = false
        }.ConnectionString;
    }
}
