using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
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

	public DbSet<ChoirEvent> Events => Set<ChoirEvent>();

	public DbSet<EventProgramme> Programmes => Set<EventProgramme>();

	public DbSet<ProgrammeRevision> ProgrammeRevisions => Set<ProgrammeRevision>();

	public DbSet<ProgrammeItem> ProgrammeItems => Set<ProgrammeItem>();

	public DbSet<Performance> Performances => Set<Performance>();

	public DbSet<ProgrammeConfirmation> ProgrammeConfirmations => Set<ProgrammeConfirmation>();

	public DbSet<PendingUpload> UploadSessions => Set<PendingUpload>();

	/// <summary>ARC-030: whole recordings of an event, each with an original and an optional playback copy.</summary>
	public DbSet<Recordings.Recording> Recordings => Set<Recordings.Recording>();

	public DbSet<Recordings.RecordingPassage> RecordingPassages => Set<Recordings.RecordingPassage>();

	/// <summary>ARC-034: text extraction work, one row per Datei.</summary>
	public DbSet<ExtractionJob> ExtractionJobs => Set<ExtractionJob>();

	public DbSet<Reading.ReadingJob> ReadingJobs => Set<Reading.ReadingJob>();

	public DbSet<Chat.ChatThread> ChatThreads => Set<Chat.ChatThread>();

	public DbSet<Chat.ChatMessage> ChatMessages => Set<Chat.ChatMessage>();

	/// <summary>ARC-022-3: usage ledger of every AI call, the source of the monthly cap.</summary>
	public DbSet<Ai.AiUsageEntry> AiUsageEntries => Set<Ai.AiUsageEntry>();

	public DbSet<Ai.AiBudgetMonth> AiBudgetMonths => Set<Ai.AiBudgetMonth>();

	/// <summary>The flat Lied (ADR-0001).</summary>
	public DbSet<Lieder.Lied> Lieder => Set<Lieder.Lied>();

	/// <summary>#166: Dateien of a Lied or an Auftritt; replaces assets and file revisions.</summary>
	public DbSet<Lieder.Datei> Dateien => Set<Lieder.Datei>();

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
