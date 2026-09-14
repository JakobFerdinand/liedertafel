using Archive.Backend.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Archive.Backend.Data;

// ARC-001 deliberately had no product entities. ARC-005 adds the first real
// tables (account/membership/code/rate-limit); each later feature owns its own migration.
public sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
	public DbSet<Account> Accounts => Set<Account>();

	public DbSet<Membership> Memberships => Set<Membership>();

	public DbSet<SignInCode> SignInCodes => Set<SignInCode>();

	public DbSet<AuthRequestLog> AuthRequestLogs => Set<AuthRequestLog>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
		=> modelBuilder.ApplyConfigurationsFromAssembly(typeof(Account).Assembly);
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
