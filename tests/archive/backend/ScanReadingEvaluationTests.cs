using System.Text.Json;
using System.Text.RegularExpressions;
using Archive.Backend.Ai;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Provenance;
using Archive.Backend.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034-1 regression set for scan reading (architecture §14: features that
/// auto-apply get a fixed set of real documents with expected fields). The
/// harness runs each document through the real reading job (PDFium, gateway,
/// budget, quote check, provenance) and reports the pass rate, the fields
/// that were applied wrongly (the dangerous case) and the cost per document.
///
/// CI runs it on generated scans with the scripted stand-in: it proves the
/// harness and never calls a provider. The live run reads the maintainer's
/// real scans and expectations from a private folder and skips when it is not
/// configured:
/// <c>ARCHIVE_SCAN_EVAL_DIR</c> (folder with the PDFs and <c>expected.json</c>),
/// <c>ARCHIVE_CHAT_ENDPOINT</c> and <c>ARCHIVE_CHAT_DEPLOYMENT_NAME</c> (az login),
/// optional <c>ARCHIVE_SCAN_EVAL_REPORT</c> (JSON report path) and the two
/// price variables of the chat evaluation for a deployment without a price.
/// <c>dotnet test tests/archive/backend --filter "Category=ScanReadingEvaluationLive"</c>.
/// </summary>
public sealed class ScanReadingEvaluationTests(ITestOutputHelper output)
{
	private const string DirectoryEnvVar = "ARCHIVE_SCAN_EVAL_DIR";
	private const string ReportEnvVar = "ARCHIVE_SCAN_EVAL_REPORT";
	private static readonly string[] FieldNames = ["composer", "lyricist", "arranger", "musical_key", "voice_label", "lyrics"];

	public sealed record EvaluationDocument(string Name, byte[] Pdf, Dictionary<string, string> Expected);

	public sealed record FieldResult(string Field, string Outcome, string? Value);

	public sealed record DocumentResult(string Name, bool Passed, long CostMicroEur, int ModelCalls, IReadOnlyList<FieldResult> Fields);

	[Fact]
	[Trait("Category", "ScanReadingEvaluation")]
	public async Task TheScriptedSetPassesAndEveryDocumentReportsItsCost()
	{
		var documents = new[]
		{
			Scripted("klar-1", pages: 1),
			Scripted("klar-2", pages: 3),
			Scripted("lang-1", pages: 5),
		};

		var results = new List<DocumentResult>();
		foreach (var document in documents)
			results.Add(await EvaluateAsync(document, new ScriptedChatClient(), ScriptedChatClient.ModelKey,
				new AiModelPrice { InputPricePerMillionEur = 0.83m, OutputPricePerMillionEur = 4.95m }));
		Report(results, "scripted");

		Assert.All(results, result => Assert.True(result.Passed, $"{result.Name}: {string.Join(", ", result.Fields.Select(f => $"{f.Field}={f.Outcome}"))}"));
		Assert.All(results, result => Assert.True(result.CostMicroEur > 0 && result.ModelCalls >= 2));
		Assert.All(results.SelectMany(r => r.Fields), field => Assert.NotEqual("applied-wrong", field.Outcome));
	}

	[Fact]
	[Trait("Category", "ScanReadingEvaluation")]
	public async Task AWrongValueThatWasAppliedFailsTheDocumentAndIsNamed()
	{
		var document = Scripted("falsch", pages: 1) with
		{
			Expected = new Dictionary<string, string> { ["composer"] = "Ludwig van Beethoven" },
		};

		var result = await EvaluateAsync(document, new ScriptedChatClient(), ScriptedChatClient.ModelKey,
			new AiModelPrice { InputPricePerMillionEur = 0.83m, OutputPricePerMillionEur = 4.95m });

		Assert.False(result.Passed);
		Assert.Equal("applied-wrong", result.Fields.Single(f => f.Field == "composer").Outcome);
	}

	/// <summary>
	/// On demand and costs a few cents per document. Reads the maintainer's
	/// real scans; without the environment it returns at once.
	/// </summary>
	[Fact]
	[Trait("Category", "ScanReadingEvaluationLive")]
	public async Task LiveRunOverTheMaintainersScansRecordsPassRateAndCost()
	{
		var directory = Environment.GetEnvironmentVariable(DirectoryEnvVar);
		var endpoint = Environment.GetEnvironmentVariable("ARCHIVE_CHAT_ENDPOINT");
		var deployment = Environment.GetEnvironmentVariable("ARCHIVE_CHAT_DEPLOYMENT_NAME");
		if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
		{
			output.WriteLine($"SCAN-EVAL live run skipped: set {DirectoryEnvVar}, ARCHIVE_CHAT_ENDPOINT and ARCHIVE_CHAT_DEPLOYMENT_NAME (az login).");
			return;
		}
		var live = AzureOpenAIChatClient.Create(new Archive.Backend.Chat.ChatOptions
			{
				Provider = AzureOpenAIChatClient.ProviderName, Endpoint = endpoint, DeploymentName = deployment,
			})
			?? throw new InvalidOperationException("Die Live-Konfiguration wählt den AzureOpenAI-Provider nicht.");
		var prices = ShippedConfiguration.Prices().FindPrice(deployment.Trim());
		if (double.TryParse(Environment.GetEnvironmentVariable("ARCHIVE_CHAT_INPUT_PRICE_EUR"), out var input)
			&& double.TryParse(Environment.GetEnvironmentVariable("ARCHIVE_CHAT_OUTPUT_PRICE_EUR"), out var outputPrice))
			prices = new AiModelPrice { InputPricePerMillionEur = (decimal)input, OutputPricePerMillionEur = (decimal)outputPrice };
		Assert.NotNull(prices);

		var documents = LoadDocuments(directory);
		Assert.NotEmpty(documents);
		var results = new List<DocumentResult>();
		foreach (var document in documents)
			results.Add(await EvaluateAsync(document, live, deployment.Trim(), prices));
		Report(results, $"live {deployment}");
		// The one hard rule: a wrong value must never have been applied by itself.
		Assert.DoesNotContain(results.SelectMany(r => r.Fields), field => field.Outcome == "applied-wrong");
	}

	private static async Task<DocumentResult> EvaluateAsync(EvaluationDocument document, IChatClient provider, string modelKey, AiModelPrice price)
	{
		var host = new ReadingTestHost(provider, modelKey: modelKey, price: price);
		var score = host.SeedScore(document.Pdf);
		await host.RunAsync(score.Revision.Id);

		var song = await host.SongAsync(score.Song.Id);
		await using var db = new ArchiveDbContext(host.DbOptions);
		var values = new Dictionary<string, string?>
		{
			["composer"] = song.Composer,
			["lyricist"] = song.Lyricist,
			["lyrics"] = song.Lyrics,
			["arranger"] = (await db.Arrangements.AsNoTracking().SingleAsync()).Arranger,
			["musical_key"] = (await db.MusicalVersions.AsNoTracking().SingleAsync()).MusicalKey,
			["voice_label"] = (await db.Assets.AsNoTracking().SingleAsync()).VoiceLabel,
		};
		var proposed = (await host.ProposalsAsync())
			.Select(p => JsonDocument.Parse(p.Payload).RootElement)
			.ToDictionary(e => e.GetProperty("field").GetString()!, e => e.GetProperty("value").GetString());
		var fields = new List<FieldResult>();
		foreach (var field in FieldNames)
		{
			var applied = values[field];
			proposed.TryGetValue(field, out var offered);
			document.Expected.TryGetValue(field, out var expected);
			var outcome = (applied, offered, expected) switch
			{
				(not null, _, null) => "applied-unchecked",
				(not null, _, _) => Matches(field, applied, expected!) ? "applied-correct" : "applied-wrong",
				(null, not null, null) => "proposed-unchecked",
				(null, not null, _) => Matches(field, offered, expected!) ? "proposed-correct" : "proposed-wrong",
				(null, null, not null) => "missing",
				_ => "empty",
			};
			if (expected is not null || applied is not null || offered is not null)
				fields.Add(new FieldResult(field, outcome, applied ?? offered));
		}
		var ledger = await host.LedgerAsync();
		var passed = fields.Where(f => document.Expected.ContainsKey(f.Field))
			.All(f => f.Outcome is "applied-correct" or "proposed-correct")
			&& fields.All(f => f.Outcome != "applied-wrong");
		return new DocumentResult(document.Name, passed, ledger.Sum(e => e.CostMicroEur), ledger.Sum(e => e.Calls),
			fields.Where(f => document.Expected.ContainsKey(f.Field) || f.Outcome.StartsWith("applied")).ToList());
	}

	/// <summary>Names, key and voice must equal; lyrics must start with the expected first line.</summary>
	private static bool Matches(string field, string actual, string expected)
	{
		var left = Normalize(actual);
		var right = Normalize(expected);
		return field == "lyrics" ? left.StartsWith(right, StringComparison.Ordinal) : left == right;
	}

	private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim().ToLowerInvariant();

	private static EvaluationDocument Scripted(string name, int pages) => new(name,
		ScanFixtures.ScanPdf(Enumerable.Range(1, pages).Select(page => new[] { $"Seite {page}" }).ToArray()),
		new Dictionary<string, string>
		{
			["composer"] = "Franz Schubert",
			["lyricist"] = "Anna Beispiel",
			["musical_key"] = "G-Dur",
			["voice_label"] = "Tenor 1",
			["lyrics"] = "Im Wald da singen die Vögel",
		});

	private static List<EvaluationDocument> LoadDocuments(string directory)
	{
		var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "expected.json"))).RootElement;
		return manifest.GetProperty("documents").EnumerateArray()
			.Select(entry => new EvaluationDocument(
				entry.GetProperty("file").GetString()!,
				File.ReadAllBytes(Path.Combine(directory, entry.GetProperty("file").GetString()!)),
				entry.GetProperty("expected").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)))
			.ToList();
	}

	private void Report(IReadOnlyList<DocumentResult> results, string label)
	{
		var passed = results.Count(r => r.Passed);
		output.WriteLine($"SCAN-EVAL {label}: {passed} of {results.Count} documents passed ({100.0 * passed / results.Count:F0} %), "
			+ $"total cost {results.Sum(r => r.CostMicroEur) / 1_000_000m:F4} EUR, "
			+ $"{results.Sum(r => r.CostMicroEur) / 1_000_000m / results.Count:F4} EUR per document.");
		foreach (var result in results)
			output.WriteLine($"SCAN-EVAL   {result.Name}: {(result.Passed ? "pass" : "FAIL")}, {result.ModelCalls} model calls, "
				+ $"{result.CostMicroEur / 1_000_000m:F4} EUR; {string.Join(", ", result.Fields.Select(f => $"{f.Field}={f.Outcome}"))}");
		var path = Environment.GetEnvironmentVariable(ReportEnvVar);
		if (!string.IsNullOrWhiteSpace(path))
			File.WriteAllText(path, JsonSerializer.Serialize(new { label, results }, new JsonSerializerOptions { WriteIndented = true }));
	}
}
