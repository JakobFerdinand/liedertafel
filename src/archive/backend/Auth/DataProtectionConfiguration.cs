using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace Archive.Backend.Auth;

/// <summary>
/// Production Data Protection key persistence (ARC-011). Development keeps
/// ignored filesystem keys under <c>Development:KeysPath</c>. Outside
/// Development the key ring lives in a private Blob object and each key is
/// wrapped by a Key Vault RSA key, so cookies and antiforgery tokens survive
/// container replacement and agree across replicas. Both clients are built
/// lazily: no Blob/Key Vault traffic happens at startup, keeping
/// <c>/alive</c> dependency-free.
/// </summary>
public static class DataProtectionConfiguration
{
	public const string BlobUriKey = "Authentication:KeysBlobUri";

	public const string KeyVaultKeyUriKey = "Authentication:KeysKeyVaultKeyUri";

	/// <summary>
	/// Test/CI escape only: keeps ephemeral in-memory keys outside
	/// Development. Production tests and the dependency-free image smoke set
	/// this; real hosted deployments must leave it unset and supply both URIs.
	/// </summary>
	public const string AllowEphemeralKeysForTestsKey = "Authentication:AllowEphemeralKeysForTests";

	public const string LegacyKeysPathKey = "Authentication:KeysPath";

	public const string ProductionApplicationName = "Liedertafel.Archive";

	public const string DevelopmentApplicationName = "Liedertafel.Archive.Development";

	public static IDataProtectionBuilder ConfigureDataProtection(
		this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
	{
		var protection = services.AddDataProtection().SetApplicationName(
			environment.IsDevelopment() ? DevelopmentApplicationName : ProductionApplicationName);
		if (environment.IsDevelopment())
		{
			var path = configuration["Development:KeysPath"]
				?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../.local/keys"));
			protection.PersistKeysToFileSystem(Directory.CreateDirectory(path));
			return protection;
		}

		if (configuration.GetValue<bool>(AllowEphemeralKeysForTestsKey))
			return protection;

		if (!string.IsNullOrWhiteSpace(configuration[LegacyKeysPathKey]))
			throw new InvalidOperationException(
				"Authentication:KeysPath ist ein Entwicklungsplatzhalter und ausserhalb von Development verboten.");
		var blobUri = configuration[BlobUriKey];
		var keyUri = configuration[KeyVaultKeyUriKey];
		if (!IsHttpsUri(blobUri) || !IsHttpsUri(keyUri))
			throw new InvalidOperationException(
				"Produktion erfordert Authentication:KeysBlobUri und Authentication:KeysKeyVaultKeyUri als https-URIs.");
		protection
			.PersistKeysToAzureBlobStorage(new Uri(blobUri!), CreateCredential())
			.ProtectKeysWithAzureKeyVault(new Uri(keyUri!), CreateCredential());
		return protection;
	}

	internal static TokenCredential CreateCredential()
	{
		var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
		// The hosted app always sets AZURE_CLIENT_ID for its user-assigned
		// identity: use it directly so no developer credential chain is
		// reachable in production. Local production-like checks without the
		// variable fall back to DefaultAzureCredential (az login).
		if (!string.IsNullOrWhiteSpace(clientId))
			return new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId.Trim()));
		return new DefaultAzureCredential();
	}

	private static bool IsHttpsUri(string? value) =>
		Uri.TryCreate(value, UriKind.Absolute, out var uri)
		&& string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
