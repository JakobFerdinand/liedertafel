using Archive.Backend.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Archive.Backend.Data;

// Identity owns users/roles/sessions; each feature slice owns its own tables
// (challenges and rate-limit evidence live here, catalogue slices add theirs).
public sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options)
	: IdentityDbContext<ArchiveUser, ArchiveRole, Guid>(options)
{
	public DbSet<SignInChallenge> SignInChallenges => Set<SignInChallenge>();

	public DbSet<AuthRequestLog> AuthRequestLogs => Set<AuthRequestLog>();

	public DbSet<MemberInvitation> MemberInvitations => Set<MemberInvitation>();

	public DbSet<MemberAdminAction> MemberAdminActions => Set<MemberAdminAction>();

	public DbSet<MemberEmailChange> MemberEmailChanges => Set<MemberEmailChange>();

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
        return new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(DatabaseConfiguration.Connection(configuration, "archive-migrations")).Options);
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
