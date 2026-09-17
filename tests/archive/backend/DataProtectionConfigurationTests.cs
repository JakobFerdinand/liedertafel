using Archive.Backend.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Archive.Backend.Tests;

public sealed class DataProtectionConfigurationTests
{
	[Fact]
	public void ProductionWithoutKeyUrisThrows()
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>()).Build();
		Assert.Throws<InvalidOperationException>(() =>
			services.ConfigureDataProtection(configuration, new TestEnvironment("Production")));
	}

	[Theory]
	[InlineData("https://stexample.blob.core.windows.net/dataprotection/keys.xml", null)]
	[InlineData(null, "https://kv-example.vault.azure.net/keys/dataprotection-wrap")]
	[InlineData("http://stexample.blob.core.windows.net/dataprotection/keys.xml", "https://kv-example.vault.azure.net/keys/dataprotection-wrap")]
	[InlineData("https://stexample.blob.core.windows.net/dataprotection/keys.xml", "not-a-uri")]
	public void ProductionWithPartialOrNonHttpsUrisThrows(string? blobUri, string? keyUri)
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["Authentication:KeysBlobUri"] = blobUri,
				["Authentication:KeysKeyVaultKeyUri"] = keyUri,
			}).Build();
		Assert.Throws<InvalidOperationException>(() =>
			services.ConfigureDataProtection(configuration, new TestEnvironment("Production")));
	}

	[Fact]
	public void ProductionForbidsLegacyFilesystemKeysPath()
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["Authentication:KeysPath"] = "/mnt/keys",
				["Authentication:KeysBlobUri"] = "https://stexample.blob.core.windows.net/dataprotection/keys.xml",
				["Authentication:KeysKeyVaultKeyUri"] = "https://kv-example.vault.azure.net/keys/dataprotection-wrap",
			}).Build();
		var error = Assert.Throws<InvalidOperationException>(() =>
			services.ConfigureDataProtection(configuration, new TestEnvironment("Production")));
		Assert.Contains("KeysPath", error.Message);
	}

	[Fact]
	public void ProductionEscapeKeepsEphemeralKeysWithoutUris()
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["Authentication:AllowEphemeralKeysForTests"] = "true",
			}).Build();
		var builder = services.ConfigureDataProtection(configuration, new TestEnvironment("Production"));
		Assert.NotNull(builder);
		using var provider = services.BuildServiceProvider();
		// Ephemeral ring must protect/unprotect without Blob/Key Vault I/O.
		var protector = provider.GetRequiredService<IDataProtectionProvider>()
			.CreateProtector("arc011-test");
		Assert.Equal("geheim", protector.Unprotect(protector.Protect("geheim")));
	}

	[Fact]
	public void ProductionWithBothHttpsUrisConfiguresWithoutNetworkIo()
	{
		var services = new ServiceCollection();
		var configuration = new ConfigurationBuilder().AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				["Authentication:KeysBlobUri"] = "https://stexample.blob.core.windows.net/dataprotection/keys.xml",
				["Authentication:KeysKeyVaultKeyUri"] = "https://kv-example.vault.azure.net/keys/dataprotection-wrap",
			}).Build();
		// Client construction is lazy: configuring the Blob/Key Vault
		// providers must not touch the network or credentials.
		var builder = services.ConfigureDataProtection(configuration, new TestEnvironment("Production"));
		Assert.NotNull(builder);
	}

	[Fact]
	public void DevelopmentKeepsFilesystemKeys()
	{
		var root = Path.Combine(Path.GetTempPath(), $"archive-dp-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			var services = new ServiceCollection();
			var configuration = new ConfigurationBuilder().AddInMemoryCollection(
				new Dictionary<string, string?>
				{
					["Development:KeysPath"] = Path.Combine(root, "keys"),
				}).Build();
			services.ConfigureDataProtection(configuration, new TestEnvironment("Development"));
			using var provider = services.BuildServiceProvider();
			var protector = provider.GetRequiredService<IDataProtectionProvider>()
				.CreateProtector("arc011-test");
			Assert.Equal("geheim", protector.Unprotect(protector.Protect("geheim")));
			Assert.True(Directory.Exists(Path.Combine(root, "keys")));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private sealed class TestEnvironment(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;
		public string ApplicationName { get; set; } = "Archive.Backend.Tests";
		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}
}
