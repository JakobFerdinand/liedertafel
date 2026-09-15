using Archive.Backend.Data;
using Archive.Backend.Development;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Auth;

/// <summary>
/// Finite operator commands. <c>--bootstrap-admin</c> creates the very first
/// administrator account through <see cref="UserManager{TUser}"/>; it refuses
/// once any user exists (repair belongs to ARC-008).
/// <c>--seed-dev-auth</c> is Development-only test data.
/// </summary>
public static class OperatorConfiguration
{
	public static string Connection(IConfiguration configuration)
	{
		var migrations = configuration.GetConnectionString("archive-migrations");
		if (!string.IsNullOrWhiteSpace(migrations))
			return DatabaseConfiguration.Connection(configuration, "archive-migrations");
		return DatabaseConfiguration.Connection(configuration, "archive-db");
	}

	public static async Task SeedDevAuthAsync(IServiceProvider provider, CancellationToken token)
	{
		var environment = provider.GetRequiredService<IHostEnvironment>();
		if (!environment.IsDevelopment())
			throw new InvalidOperationException("Local service commands require Development.");
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Operator");
		var accounts = await AuthSeed.EnsureTestAccountsAsync(users, roles, token);
		logger.LogInformation("Development test accounts ensured ({Count})", accounts.Count);
	}

	public static async Task BootstrapAdminAsync(
		IServiceProvider provider, IConfiguration configuration, string[] args, CancellationToken token)
	{
		var environment = provider.GetRequiredService<IHostEnvironment>();
		var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Archive.Operator");
		var email = Option(args, "--email");
		var displayName = Option(args, "--name");
		var operatorName = Option(args, "--operator") ?? "Betreiber";
		if (!AuthSecurity.TryNormalizeEmail(email, out var normalized))
			throw new InvalidOperationException("Bootstrap requires --email <address>.");
		if (!environment.IsDevelopment())
		{
			var expected = configuration["Archive:OperatorToken"];
			var provided = Option(args, "--operator-token");
			if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(provided)
				|| !CryptographicEquals(expected, provided))
				throw new InvalidOperationException("Bootstrap outside Development requires Archive:OperatorToken and --operator-token.");
		}
		var users = provider.GetRequiredService<UserManager<ArchiveUser>>();
		var roles = provider.GetRequiredService<RoleManager<ArchiveRole>>();
		var db = provider.GetRequiredService<ArchiveDbContext>();
		var time = provider.GetRequiredService<TimeProvider>();
		if (await db.Users.AnyAsync(token))
			throw new InvalidOperationException("Archive already has accounts; use member administration instead of bootstrap.");
		if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
		{
			try
			{
				var created = await roles.CreateAsync(new ArchiveRole(ArchiveRoles.Administrator));
				if (!created.Succeeded && !await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be created.");
			}
			catch (DbUpdateException)
			{
				// A parallel bootstrap may have won the race; only the
				// still-missing role is an error.
				if (!await roles.RoleExistsAsync(ArchiveRoles.Administrator))
					throw;
			}
		}
		var id = await AuthSeed.EnsureUserAsync(users, email!.Trim(), displayName, token);
		var admin = (await users.FindByIdAsync(id.ToString()))!;
		if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
		{
			try
			{
				var inRole = await users.AddToRoleAsync(admin, ArchiveRoles.Administrator);
				if (!inRole.Succeeded && !await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw new InvalidOperationException("Administrator role could not be assigned.");
			}
			catch (DbUpdateException)
			{
				if (!await users.IsInRoleAsync(admin, ArchiveRoles.Administrator))
					throw;
			}
		}
		// Log the domain only; never the full address from an operator command.
		logger.LogInformation(
			"Bootstrap administrator ensured for domain {Domain} by {Operator}",
			AuthSecurity.DomainOf(normalized).ToLowerInvariant(), operatorName);
	}

	private static string? Option(string[] args, string name)
	{
		for (var i = 0; i < args.Length; i++)
		{
			if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				return args[i + 1];
		}
		return null;
	}

	private static bool CryptographicEquals(string a, string b)
	{
		var ab = System.Text.Encoding.UTF8.GetBytes(a);
		var bb = System.Text.Encoding.UTF8.GetBytes(b);
		return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ab, bb);
	}
}
