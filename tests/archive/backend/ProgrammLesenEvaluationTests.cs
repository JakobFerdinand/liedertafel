using System.Runtime.CompilerServices;
using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Chat;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Archive.Backend.Reading;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Archive.Backend.Tests;

/// <summary>
/// #131 regression set for reading a programme scan into an Auftritt
/// (architecture §14: features that suggest from documents get a fixed set of
/// documents with expected values). The harness runs each document through the
/// real <see cref="ProgrammReader"/> (gateway, budget, quote check, catalogue
/// matcher) and reports, per document, the header values that were wrong, how
/// every Programm entry ended up (matched correctly, matched wrongly - the
/// dangerous case -, offered as a candidate, left unmatched, missed) and the
/// cost.
///
/// CI runs it on generated photographs with the scripted stand-in: the
/// stand-in "transcribes" a fixed text per document and reads it with the
/// scripted programme agent, so the harness, the checks and the matcher are
/// exercised without a provider. The live run reads the maintainer's real
/// programme scans from a private folder and skips when it is not configured:
/// <c>ARCHIVE_PROGRAMM_EVAL_DIR</c> (folder with the scans and
/// <c>expected.json</c>: <c>{"lieder":[titles],"documents":[{"file":..,"expected":
/// {"title":..,"year":..,"month":..,"day":..,"approximate":..,"venue":..,
/// "programm":[{"text":..,"lied":title|null}]}}]}</c>), <c>ARCHIVE_CHAT_ENDPOINT</c>
/// and <c>ARCHIVE_CHAT_DEPLOYMENT_NAME</c> (az login), optional
/// <c>ARCHIVE_PROGRAMM_EVAL_REPORT</c> (JSON report path) and the two price
/// variables of the chat evaluation for a deployment without a price.
/// <c>dotnet test tests/archive/backend --filter "Category=ProgrammReadingEvaluationLive"</c>.
/// </summary>
public sealed class ProgrammLesenEvaluationTests(ITestOutputHelper output)
{
	private const string DirectoryEnvVar = "ARCHIVE_PROGRAMM_EVAL_DIR";
	private const string ReportEnvVar = "ARCHIVE_PROGRAMM_EVAL_REPORT";

	public sealed record ExpectedEntry(string Text, string? Lied);

	public sealed record Expected(
		string? Title, int? Year, int? Month, int? Day, bool Approximate, string? Venue, IReadOnlyList<ExpectedEntry> Programm);

	/// <summary><paramref name="Transcription"/> is what the scripted stand-in "reads" from the image; null for live documents.</summary>
	public sealed record EvaluationDocument(
		string Name, byte[] File, string? Transcription, Expected Expected, IReadOnlyList<string>? ExtraLieder = null);

	public sealed record Evaluation(IReadOnlyList<string> Catalogue, IReadOnlyList<EvaluationDocument> Documents);

	public sealed record EntryResult(string Text, string Outcome);

	public sealed record DocumentResult(
		string Name, bool Passed, long CostMicroEur, int ModelCalls, IReadOnlyList<string> WrongHeader, IReadOnlyList<EntryResult> Entries);

	private static readonly string[] Catalogue =
	[
		"Ave Maria", "Der Mond ist aufgegangen", "Süßer die Glocken nie klingen", "Wanderlust",
		"Stille Nacht, heilige Nacht", "O du fröhliche", "Heidenröslein", "Im Wald und auf der Heide",
	];

	[Fact]
	[Trait("Category", "ProgrammReadingEvaluation")]
	public async Task TheScriptedSetPassesWithoutAWrongMatchAndReportsEveryEntry()
	{
		var results = new List<DocumentResult>();
		foreach (var document in ScriptedDocuments())
			results.Add(await EvaluateAsync(document, Catalogue, new ProgrammEvalClient(document.Transcription), ScriptedChatClient.ModelKey, ScriptedPrice));
		Report(results, "scripted");

		Assert.All(results, result => Assert.True(result.Passed,
			$"{result.Name}: {string.Join(", ", result.WrongHeader)} {string.Join(", ", result.Entries.Select(e => $"{e.Text}={e.Outcome}"))}"));
		// The one hard rule: a wrong Lied must never have been picked by itself.
		Assert.DoesNotContain(results.SelectMany(r => r.Entries), e => e.Outcome == "matched-wrong");
		Assert.All(results.Where(r => r.Name != "leer"), result => Assert.True(result.CostMicroEur > 0 && result.ModelCalls >= 2));
	}

	[Fact]
	[Trait("Category", "ProgrammReadingEvaluation")]
	public async Task AWrongPreselectionIsNamedAndFailsTheDocument()
	{
		// The scripted catalogue has "Wanderlust"; claiming that "Wanderlied" must be matched to it is wrong.
		var document = new EvaluationDocument("falsch", ScanFixtures.PageJpeg(["Programm"]),
			"Konzert\n1. Der Mond ist aufgegangn",
			new Expected(null, null, null, null, false, null, [new ExpectedEntry("Der Mond ist aufgegangn", "Im Wald und auf der Heide")]));

		var result = await EvaluateAsync(document, Catalogue, new ProgrammEvalClient(document.Transcription), ScriptedChatClient.ModelKey, ScriptedPrice);

		Assert.False(result.Passed);
		Assert.Equal("matched-wrong", result.Entries.Single().Outcome);
	}

	[Fact]
	[Trait("Category", "ProgrammReadingEvaluation")]
	public async Task AnEntryTheModelInventedFailsTheDocument()
	{
		var document = new EvaluationDocument("erfunden", ScanFixtures.PageJpeg(["Programm"]),
			"Konzert\n1. Ave Maria\n2. Waldfahrt",
			new Expected(null, null, null, null, false, null, [new ExpectedEntry("Ave Maria", "Ave Maria")]));

		var result = await EvaluateAsync(document, Catalogue, new ProgrammEvalClient(document.Transcription), ScriptedChatClient.ModelKey, ScriptedPrice);

		Assert.False(result.Passed);
		Assert.Contains(result.Entries, e => e.Outcome == "extra");
	}

	/// <summary>
	/// On demand and costs a few cents per document. Reads the maintainer's
	/// real programme scans; without the environment it returns at once.
	/// </summary>
	[Fact]
	[Trait("Category", "ProgrammReadingEvaluationLive")]
	public async Task LiveRunOverTheMaintainersProgrammScansRecordsPassRateAndCost()
	{
		var directory = Environment.GetEnvironmentVariable(DirectoryEnvVar);
		var endpoint = Environment.GetEnvironmentVariable("ARCHIVE_CHAT_ENDPOINT");
		var deployment = Environment.GetEnvironmentVariable("ARCHIVE_CHAT_DEPLOYMENT_NAME");
		if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
		{
			output.WriteLine($"PROGRAMM-EVAL live run skipped: set {DirectoryEnvVar}, ARCHIVE_CHAT_ENDPOINT and ARCHIVE_CHAT_DEPLOYMENT_NAME (az login).");
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

		var evaluation = LoadEvaluation(directory);
		Assert.NotEmpty(evaluation.Documents);
		var results = new List<DocumentResult>();
		foreach (var document in evaluation.Documents)
			results.Add(await EvaluateAsync(document, evaluation.Catalogue, live, deployment.Trim(), prices));
		Report(results, $"live {deployment}");
		Assert.DoesNotContain(results.SelectMany(r => r.Entries), e => e.Outcome == "matched-wrong");
	}

	private static readonly AiModelPrice ScriptedPrice = new() { InputPricePerMillionEur = 0.83m, OutputPricePerMillionEur = 4.95m };

	private static async Task<DocumentResult> EvaluateAsync(
		EvaluationDocument document, IReadOnlyList<string> catalogue, IChatClient provider, string modelKey, AiModelPrice price)
	{
		var host = new ReadingTestHost(provider, modelKey: modelKey, price: price);
		var account = Guid.CreateVersion7();
		var now = DateTimeOffset.UtcNow;
		foreach (var title in catalogue.Concat(document.ExtraLieder ?? []))
		{
			host.Db.Lieder.Add(new Lied
			{
				Title = title, CreatedByAccountId = account, UpdatedByAccountId = account, CreatedAt = now, UpdatedAt = now, RowVersion = 1,
			});
		}
		await host.Db.SaveChangesAsync();
		var titles = host.Db.Lieder.ToDictionary(l => l.Id, l => l.Title);
		var reader = new ProgrammReader(host.ScanModel, host.Renderer, host.Db,
			Microsoft.Extensions.Options.Options.Create(host.Options), Microsoft.Extensions.Options.Options.Create(new ExtractionOptions()));

		var lesung = await reader.ReadAsync(document.File, account, CancellationToken.None);

		var expected = document.Expected;
		var read = lesung.Felder.ToDictionary(f => f.Field, f => f.Value);
		var wrong = new List<string>();
		Check(wrong, "title", expected.Title, read.GetValueOrDefault("title"));
		Check(wrong, "jahr", expected.Year?.ToString(), read.GetValueOrDefault("jahr"));
		Check(wrong, "monat", expected.Month?.ToString(), read.GetValueOrDefault("monat"));
		Check(wrong, "tag", expected.Day?.ToString(), read.GetValueOrDefault("tag"));
		Check(wrong, "venue", expected.Venue, read.GetValueOrDefault("venue"));
		if (expected.Approximate != read.ContainsKey("unsicher"))
			wrong.Add("unsicher");

		var entries = new List<EntryResult>();
		for (var index = 0; index < Math.Max(expected.Programm.Count, lesung.Programm.Count); index++)
		{
			if (index >= lesung.Programm.Count)
			{
				entries.Add(new EntryResult(expected.Programm[index].Text, "read-missing"));
				continue;
			}
			var entry = lesung.Programm[index];
			if (index >= expected.Programm.Count)
			{
				entries.Add(new EntryResult(entry.Text, "extra"));
				continue;
			}
			var want = expected.Programm[index].Lied;
			var picked = entry.Match.Lied is { } lied ? titles[lied.Id] : null;
			var offered = entry.Match.Candidates.Select(c => titles[c.Id]).ToList();
			var outcome = (picked, want) switch
			{
				(not null, null) => "matched-wrong",
				(not null, _) => picked == want ? (entry.Match.Sure ? "matched-correct" : "matched-unsure-correct") : "matched-wrong",
				(null, null) => "unmatched-correct",
				_ => offered.Contains(want) ? "candidates-correct" : "unmatched-missed",
			};
			entries.Add(new EntryResult(entry.Text, outcome));
		}
		var ledger = await host.LedgerAsync();
		var passed = wrong.Count == 0 && entries.All(e => e.Outcome is not ("matched-wrong" or "extra" or "read-missing"));
		return new DocumentResult(document.Name, passed, ledger.Sum(e => e.CostMicroEur), ledger.Sum(e => e.Calls), wrong, entries);
	}

	private static void Check(List<string> wrong, string field, string? expected, string? actual)
	{
		if (actual is not null && !string.Equals(expected, actual, StringComparison.Ordinal))
			wrong.Add($"{field}: erwartet {expected ?? "nichts"}, gelesen {actual}");
		else if (actual is null && expected is not null)
			wrong.Add($"{field}: nicht gelesen");
	}

	private static EvaluationDocument Scripted(string name, string transcription, Expected expected, params string[] extraLieder)
		=> new(name, ScanFixtures.PageJpeg(transcription.Split('\n')), transcription, expected, extraLieder);

	private static IEnumerable<EvaluationDocument> ScriptedDocuments()
	{
		yield return Scripted("klar-konzert",
			"Frühjahrskonzert der Liedertafel\nSamstag, 21. März 1998\nOrt: Pfarrsaal Mining\n1. Ave Maria\n2. Der Mond ist aufgegangen\n3. Süßer die Glocken nie klingen",
			new Expected("Frühjahrskonzert der Liedertafel", 1998, 3, 21, false, "Pfarrsaal Mining",
				[new("Ave Maria", "Ave Maria"), new("Der Mond ist aufgegangen", "Der Mond ist aufgegangen"), new("Süßer die Glocken nie klingen", "Süßer die Glocken nie klingen")]));
		yield return Scripted("ocr-rauschen",
			"Konzert der Liedertafel\n12. Mai 1951\nOrt: Gasthaus zur Post\n1. Der Mond ist aufgegangn\n2. Siisser die Glocken nie klingen\n3. Wanderlied\n4. Heidenroeslein",
			new Expected("Konzert der Liedertafel", 1951, 5, 12, false, "Gasthaus zur Post",
				[new("Der Mond ist aufgegangn", "Der Mond ist aufgegangen"), new("Siisser die Glocken nie klingen", "Süßer die Glocken nie klingen"),
					new("Wanderlied", null), new("Heidenroeslein", "Heidenröslein")]));
		yield return Scripted("ungefaehres-jahr",
			"Sängerfest\num 1950\n1. Ave Maria",
			new Expected("Sängerfest", 1950, null, null, true, null, [new("Ave Maria", "Ave Maria")]));
		yield return Scripted("nur-monat",
			"Weihnachtsfeier der Liedertafel\nDezember 1938\nGasthaus Post\n1. Stille Nacht\n2. O du fröhliche",
			new Expected("Weihnachtsfeier der Liedertafel", 1938, 12, null, false, "Gasthaus Post",
				[new("Stille Nacht", "Stille Nacht, heilige Nacht"), new("O du fröhliche", "O du fröhliche")]));
		yield return Scripted("doppelter-titel",
			"Gottesdienst\n3. Oktober 1965\n1. Ave Maria",
			new Expected("Gottesdienst", 1965, 10, 3, false, null, [new("Ave Maria", "Ave Maria")]),
			"Ave Maria");
		yield return Scripted("unbekannte-lieder",
			"Konzert\n1. Ein Lied das es nicht gibt\n2. Noch ein Lied",
			new Expected("Konzert", null, null, null, false, null, [new("Ein Lied das es nicht gibt", null), new("Noch ein Lied", null)]));
		yield return new EvaluationDocument("leer", ScanFixtures.PageJpeg(["Leer"]), "",
			new Expected(null, null, null, null, false, null, []));
	}

	private static Evaluation LoadEvaluation(string directory)
	{
		var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "expected.json"))).RootElement;
		var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
		var catalogue = manifest.GetProperty("lieder").EnumerateArray().Select(e => e.GetString()!).ToList();
		var documents = manifest.GetProperty("documents").EnumerateArray()
			.Select(entry => new EvaluationDocument(
				entry.GetProperty("file").GetString()!,
				File.ReadAllBytes(Path.Combine(directory, entry.GetProperty("file").GetString()!)),
				null,
				entry.GetProperty("expected").Deserialize<Expected>(options)!))
			.ToList();
		return new Evaluation(catalogue, documents);
	}

	private void Report(IReadOnlyList<DocumentResult> results, string label)
	{
		var passed = results.Count(r => r.Passed);
		var entries = results.SelectMany(r => r.Entries).GroupBy(e => e.Outcome).OrderBy(g => g.Key)
			.Select(g => $"{g.Key}={g.Count()}");
		output.WriteLine($"PROGRAMM-EVAL {label}: {passed} of {results.Count} documents passed ({100.0 * passed / results.Count:F0} %), "
			+ $"entries: {string.Join(", ", entries)}, total cost {results.Sum(r => r.CostMicroEur) / 1_000_000m:F4} EUR.");
		foreach (var result in results)
			output.WriteLine($"PROGRAMM-EVAL   {result.Name}: {(result.Passed ? "pass" : "FAIL")}, {result.ModelCalls} model calls, "
				+ $"{result.CostMicroEur / 1_000_000m:F4} EUR; header: {(result.WrongHeader.Count == 0 ? "ok" : string.Join("; ", result.WrongHeader))}; "
				+ $"{string.Join(", ", result.Entries.Select(e => $"{e.Text}={e.Outcome}"))}");
		var path = Environment.GetEnvironmentVariable(ReportEnvVar);
		if (!string.IsNullOrWhiteSpace(path))
			File.WriteAllText(path, JsonSerializer.Serialize(new { label, results }, new JsonSerializerOptions { WriteIndented = true }));
	}

	/// <summary>
	/// The scripted stand-in with a per-document "photograph": it cannot see
	/// images, so the programme transcription answers the document's text and
	/// everything else is the shipped scripted client.
	/// </summary>
	private sealed class ProgrammEvalClient(string? transcription) : IChatClient
	{
		private readonly ScriptedChatClient inner = new();

		public async Task<ChatResponse> GetResponseAsync(
			IEnumerable<AiChatMessage> messages, AiChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			var updates = new List<ChatResponseUpdate>();
			await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
				updates.Add(update);
			return updates.ToChatResponse();
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
			IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			var list = messages.ToList();
			if (transcription is not null && ScriptedScanReader.IsProgrammTranscription(list, options))
			{
				yield return new ChatResponseUpdate(ChatRole.Assistant, "=== Seite 1 ===\n" + transcription);
				yield return new ChatResponseUpdate
				{
					Contents = [new UsageContent(new UsageDetails { InputTokenCount = 1700, OutputTokenCount = 120 })],
				};
				yield break;
			}
			await foreach (var update in inner.GetStreamingResponseAsync(list, options, cancellationToken))
				yield return update;
		}

		public object? GetService(Type serviceType, object? serviceKey = null) => null;

		public void Dispose() => inner.Dispose();
	}
}
