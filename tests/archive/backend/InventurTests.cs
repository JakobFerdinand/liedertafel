using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Inventur;
using InventurWalks = Archive.Backend.Inventur.Inventur;
using Archive.Backend.Reading;

namespace Archive.Backend.Tests;

/// <summary>
/// #98 Quellen-Inventur: the deterministic walker over a sanitized fixture
/// corpus that generalizes the observed Drive conventions (Sample A flat score
/// folder, Sample B file-type subfolders with PT = Partitur, Sample C duplicate
/// voice files, MIDI, shortcut, spreadsheet). The corpus lives under
/// tests/archive/fixtures/drive-inventur; a committed mapping.json declares how
/// the bulk import (#143) maps it to Lieder and Dateien, and these tests keep
/// the mapping, corpus and tool output in agreement.
/// </summary>
public sealed class InventurTests
{
	private static string FixtureRoot()
	{
		for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
		{
			var candidate = Path.Combine(current.FullName, "tests", "archive", "fixtures", "drive-inventur");
			if (Directory.Exists(candidate))
				return candidate;
		}
		throw new InvalidOperationException("Das Fixture-Korpus fehlt unter tests/archive/fixtures/drive-inventur.");
	}

	/// <summary>Reading assumptions of the AI reading job with Luna's configured price pair.</summary>
	private static InventurSettings LunaSettings() => new(
		new ReadingOptions(),
		new AiOptions
		{
			Models = new Dictionary<string, AiModelPrice>(StringComparer.OrdinalIgnoreCase)
			{
				["gpt-6-luna"] = new AiModelPrice { InputPricePerMillionEur = 0.1056m, OutputPricePerMillionEur = 0.528m },
			},
		});

	/// <summary>
	/// Copies the corpus without its meta files (README, mapping) into a fresh
	/// directory, empty subfolders included; the walk runs on a copy so tests
	/// cannot mutate the committed corpus.
	/// </summary>
	private static string FixtureCopy()
	{
		var temp = Path.Combine(Path.GetTempPath(), "inventur-fixture-" + Guid.NewGuid().ToString("N"));
		foreach (var directory in Directory.EnumerateDirectories(FixtureRoot(), "*", SearchOption.AllDirectories))
			Directory.CreateDirectory(Path.Combine(temp, Path.GetRelativePath(FixtureRoot(), directory)));
		foreach (var file in Directory.EnumerateFiles(FixtureRoot(), "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(FixtureRoot(), file);
			if (relative is "mapping.json" or "README.md")
				continue;
			File.Copy(file, Path.Combine(temp, relative));
		}
		return temp;
	}

	private static InventurRun FixtureRun()
	{
		var temp = FixtureCopy();
		try
		{
			return InventurWalks.Run(temp, "gpt-6-luna", LunaSettings());
		}
		finally
		{
			Directory.Delete(temp, recursive: true);
		}
	}

	[Fact]
	public void WalksTheFixtureCorpusWithExactTotals()
	{
		var run = FixtureRun();

		Assert.Equal(23, run.Totals.FileCount);
		Assert.Equal(29634, run.Totals.Bytes);
		Assert.Equal(7, run.Totals.FolderCount);
		Assert.Equal(
			["Sample B/2. Media", "Sample B/3. Notierprogrammdaten"],
			run.Totals.EmptyFolders);
		// The largest file leads the resumable-upload view of the corpus.
		Assert.Equal(
			[
				new InventurLargest("Sample B/1. Noten/PT - Sample B.pdf", 14828),
				new InventurLargest("Sample C/Sample C B1 (1).pdf", 2027),
				new InventurLargest("Sample C/Sample C Partitur.pdf", 2026),
			],
			run.Largest.Take(3).ToList());
	}

	[Fact]
	public void SeparatesExtractableTextFromScannedPdfs()
	{
		var run = FixtureRun();

		Assert.Equal(12, run.Pdf.Files);
		Assert.Equal(11, run.Pdf.EmbeddedText);
		Assert.Equal(1, run.Pdf.Scanned);
		Assert.Equal(3, run.Pdf.ScannedPages);
		Assert.Empty(run.Pdf.Unreadable);

		// The projected scan case stands in for the real sample B PDF whose
		// content has not been inspected yet (only its ~297 KB display size).
		var scan = Assert.Single(run.Files, f => f.Pdf is { Text: "scan" });
		Assert.Equal("Sample B/1. Noten/PT - Sample B.pdf", scan.Path);
		Assert.Equal(3, scan.Pdf!.Pages);
		// Sample C's duplicate B1 pair differs in revision content, not only bytes: 1 page vs 4.
		Assert.Equal(1, Assert.Single(run.Files, f => f.Path == "Sample C/Sample C B1.pdf").Pdf!.Pages);
		Assert.Equal(4, Assert.Single(run.Files, f => f.Path == "Sample C/Sample C B1 (1).pdf").Pdf!.Pages);
	}

	[Fact]
	public void DetectsMediaKindsFromLeadingBytes()
	{
		var run = FixtureRun();

		// Sample C's five MP3s are playable audio by their leading bytes.
		var mp3s = run.Files.Where(f => f.ContentType == "audio/mpeg").ToList();
		Assert.Equal(5, mp3s.Count);
		Assert.All(mp3s, f => Assert.True(f.Playable));
		Assert.Equal(
			["Sample C/Sample C B1.mp3", "Sample C/Sample C B2.mp3",
				"Sample C/Sample C Partituraufnahme.mp3", "Sample C/Sample C T1.mp3", "Sample C/Sample C T2.mp3"],
			mp3s.Select(f => f.Path).Order().ToList());

		// MIDI presence: exactly one Übe-Datei candidate under Midi-Beispiel.
		var midi = Assert.Single(run.Files, f => f.Flags.Contains("midi"));
		Assert.Equal("Midi-Beispiel/Beispiel Uebefassung.mid", midi.Path);
		Assert.Equal("audio/midi", midi.ContentType);
		Assert.Equal(1, run.Media.MidiFiles);

		// The .xls catalogue in Sample A is an OLE compound file, never a score.
		var catalog = Assert.Single(run.Files, f => f.Flags.Contains("oleCompoundFile"));
		Assert.Equal("Sample A/Sample A Verzeichnis.xls", catalog.Path);

		// The notation-source candidate is a text document and never MIDI evidence.
		var capx = Assert.Single(run.Files, f => f.Path == "Sample C/Sample C 3.capx");
		Assert.Contains("textLike", capx.Flags);
		Assert.DoesNotContain("midi", capx.Flags);

		// The archive root lists both the folder and a shortcut to Sample A.
		var shortcut = Assert.Single(run.Files, f => f.Flags.Contains("shortcut"));
		Assert.Equal("Verknüpfung - Sample A.url", shortcut.Path);
		Assert.Equal("PLACEHOLDER-SOURCE-FOLDER-LINK", shortcut.LinkTarget);
	}

	[Fact]
	public void FlagsUnresolvedDuplicatesWithoutPreferringTheNewerFile()
	{
		var run = FixtureRun();

		// Sample C: two B1 PDFs with different sizes (real: ~21 KB vs ~316 KB);
		// neither byte equality nor a correction relationship is established.
		var group = Assert.Single(run.Duplicates);
		Assert.Equal("Sample C", group.Folder);
		Assert.Equal("Sample C B1", group.Stem);
		Assert.Equal(
			["Sample C/Sample C B1 (1).pdf", "Sample C/Sample C B1.pdf"],
			group.Paths);
		foreach (var entry in run.Files.Where(f => group.Paths.Contains(f.Path)))
			Assert.Contains("unresolvedDuplicateCandidate", entry.Flags);
	}

	[Fact]
	public void EstimatesReadingCostAnalyticallyFromScanPages()
	{
		var run = FixtureRun();

		// One scanned document of 3 pages fits inside one 10-image call.
		Assert.Equal("gpt-6-luna", run.ReadingCost.Model);
		Assert.Equal(1, run.ReadingCost.ScannedDocuments);
		Assert.Equal(3, run.ReadingCost.ScannedPagesRead);
		Assert.Equal(1, run.ReadingCost.Calls);
		Assert.Equal(26_000, run.ReadingCost.InputTokens);
		Assert.Equal(5_000, run.ReadingCost.OutputTokens);
		Assert.Equal(0.0053856m, run.ReadingCost.EstimatedEur);
	}

	[Fact]
	public void CapsReadingCostPerPageLimitsOfTheReadingJob()
	{
		// The reading job only ever reads the first MaxPagesPerRequest pages;
		// the estimate must bound the model input to the job's own limit.
		var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "inventur-" + Guid.NewGuid().ToString("N")));
		Directory.CreateDirectory(Path.Combine(root.FullName, "Nur Ein Scan"));
		File.WriteAllBytes(
			Path.Combine(root.FullName, "Nur Ein Scan", "Lang mit 23 Seiten.pdf"),
			ScanFixtures.ScanPdf(Enumerable.Range(0, 23).Select(_ => new[] { "Scanzeile" }).ToArray()));
		try
		{
			var run = InventurWalks.Run(root.FullName, "gpt-6-luna", LunaSettings());

			Assert.Equal(23, run.Pdf.ScannedPages);
			// Only the first 20 pages are ever read: two calls of ten images.
			Assert.Equal(20, run.ReadingCost.ScannedPagesRead);
			Assert.Equal(2, run.ReadingCost.Calls);
			Assert.Equal(52_000, run.ReadingCost.InputTokens);
			Assert.Equal(10_000, run.ReadingCost.OutputTokens);
			Assert.Equal(0.0107712m, run.ReadingCost.EstimatedEur);
		}
		finally
		{
			root.Delete(recursive: true);
		}
	}

	[Fact]
	public void CommandWritesJsonReportWithConfiguredModel()
	{
		var report = Path.Combine(Path.GetTempPath(), "inventur-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			var output = new StringWriter();
			var code = InventurCommand.Run(
				[FixtureCopy(), "--model", "fehlt-im-preisverzeichnis", "--out", report], output);

			Assert.Equal(0, code);
			var document = JsonDocument.Parse(File.ReadAllText(report));
			var totals = document.RootElement.GetProperty("totals");
			Assert.Equal(23, totals.GetProperty("fileCount").GetInt32());
			Assert.Equal(29634, totals.GetProperty("bytes").GetInt64());
			Assert.Equal("fehlt-im-preisverzeichnis", document.RootElement.GetProperty("readingCost").GetProperty("model").GetString());
			// Without an Archive:Ai price in this test host the command answers
			// honestly null instead of inventing a token price.
			Assert.Equal(JsonValueKind.Null,
				document.RootElement.GetProperty("readingCost").GetProperty("estimatedEur").ValueKind);
			var stdout = output.ToString();
			Assert.Contains("Dateien", stdout);
			Assert.Contains("fehlt-im-preisverzeichnis", stdout);
		}
		finally
		{
			File.Delete(report);
		}
	}

	[Fact]
	public void CommandRejectsAMissingDirectoryWithUsage()
	{
		var output = new StringWriter();
		var code = InventurCommand.Run(
			[Path.Combine(FixtureRoot(), "fehlt"), "--model", "gpt-6-luna"], output);

		Assert.NotEqual(0, code);
		Assert.Contains("Usage", output.ToString());
	}

	[Fact]
	public void MappingFileStaysTruthfulToTheFixtureCorpus()
	{
		var run = FixtureRun();
		var mapping = JsonDocument.Parse(
			File.ReadAllText(Path.Combine(FixtureRoot(), "mapping.json"))).RootElement;

		var declared = new List<(string Path, JsonElement Entry)>();
		foreach (var lied in mapping.GetProperty("lieder").EnumerateArray())
			foreach (var datei in lied.GetProperty("dateien").EnumerateArray())
				declared.Add((datei.GetProperty("path").GetString()!, datei));
		foreach (var otherwise in mapping.GetProperty("ohneLiedzuordnung").EnumerateArray())
			declared.Add((otherwise.GetProperty("path").GetString()!, otherwise));

		// Every fixture file is covered exactly once, and paths must match the
		// walker's '/'-relative convention so the bulk import (#143) consumes
		// the file as is.
		var toolPaths = run.Files.Select(f => f.Path).Order().ToList();
		var mappingPaths = declared.Select(d => d.Path).Order().ToList();
		Assert.Equal(toolPaths, mappingPaths);

		foreach (var (path, entry) in declared)
		{
			var file = Assert.Single(run.Files, f => f.Path == path);
			if (entry.TryGetProperty("contentType", out var claimed) && claimed.ValueKind == JsonValueKind.String)
				Assert.Equal(claimed.GetString(), file.ContentType);
			var flags = entry.TryGetProperty("flags", out var raw)
				? raw.EnumerateArray().Select(f => f.GetString()!).ToList()
				: [];
			Assert.Equal(flags.Order(), file.Flags.Order());
		}
	}
}
