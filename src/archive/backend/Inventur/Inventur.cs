using Archive.Backend.Ai;
using Archive.Backend.Extraction;
using Archive.Backend.Reading;

namespace Archive.Backend.Inventur;

/// <summary>The configuration the analytical cost estimate reads: the reading job's own bounds and the <c>Archive:Ai</c> prices.</summary>
public sealed record InventurSettings(ReadingOptions Reading, AiOptions Ai);

/// <summary>
/// #98 Quellen-Inventur: the deterministic walk over one directory. No
/// database, queue or model: file counts, aggregate and largest sizes,
/// leading-byte container sniffs, MIDI presence, per-PDF PDFium page counts
/// with PdfPig extractable-text classification, duplicate-name candidates and
/// the analytical scan-reading cost estimate. The result serves the
/// maintainer's real-source measurement and #143's fixture mapping.
/// </summary>
/// <summary>Group key for duplicate-name detection; record equality is the comparison.</summary>
public sealed record InventurNameKey(string? Folder, string Stem, string Extension);

public static class Inventur
{
	/// <summary>Entries of the aggregate largest-files view handed to the upload work.</summary>
	public const int LargestLimit = 20;

	/// <summary>Prompt tokens of one transcription call, as reserved by the reading session's worst-case basis in <c>ScanReadingModel.WorstCaseTranscription</c>.</summary>
	private const int PromptTokensPerCall = 1000;

	public static InventurRun Run(string root, string model, InventurSettings settings)
	{
		var fullRoot = Path.GetFullPath(root);
		if (!Directory.Exists(fullRoot))
			throw new DirectoryNotFoundException($"Das Verzeichnis '{root}' existiert nicht.");

		var directories = new List<string>();
		var emptyFolders = new List<string>();
		foreach (var directory in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories))
		{
			// A folder counts empty only with no entries at all, mirroring the
			// observed Drive listing convention ("(empty in the listing)"):
			// empty classification folders import no records of their own.
			directories.Add(directory);
			// A committed .gitkeep stands for an empty folder in a real
			// checkout; it never makes the folder non-empty for the walk.
			if (!Directory.EnumerateFileSystemEntries(directory).Any(e => Path.GetFileName(e) != ".gitkeep"))
				emptyFolders.Add(Relative(fullRoot, directory));
		}
		emptyFolders.Sort(StringComparer.Ordinal);

		var walked = new List<(string Path, long Bytes, string FileName, InventurFileFinding Detection)>();
		foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
		{
			using var stream = File.OpenRead(file);
			var header = ReadHeader(stream);
			walked.Add((Relative(fullRoot, file), stream.Length, Path.GetFileName(file),
				InventurDetection.Classify(file, header)));
		}

		// Same-name groups resolve to flags before entries exist, because the
		// file entries are immutable records (the walk never edits its facts).
		var duplicateFlags = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var group in walked
			.GroupBy(w => new InventurNameKey(
				Path.GetDirectoryName(w.Path),
				InventurDetection.DuplicateStem(w.FileName).ToLowerInvariant(),
				Path.GetExtension(w.FileName).ToLowerInvariant()))
			.Where(g => g.Count() > 1))
		{
			var flag = group.DistinctBy(w => w.Bytes).Count() > 1
				? InventurDetection.UnresolvedDuplicateFlag
				: InventurDetection.SameSizeDuplicateFlag;
			foreach (var member in group)
				duplicateFlags[member.Path] = flag;
		}

		var files = new List<InventurFileEntry>();
		foreach (var (path, bytes, fileName, detection) in walked)
		{
			var flags = new List<string>(detection.Flags);
			if (duplicateFlags.TryGetValue(path, out var duplicateFlag))
				flags.Add(duplicateFlag);
			var linkTarget = detection.Flags.Contains(InventurDetection.ShortcutFlag) && fileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
				? ReadShortcutLinkTarget(Path.Combine(fullRoot, path))
				: null;
			var pdf = detection.ContentType == "application/pdf" ? AnalyzePdf(Path.Combine(fullRoot, path)) : null;
			files.Add(new InventurFileEntry(path, bytes, detection.ContentType, detection.Playable, linkTarget, flags, pdf));
		}
		files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));

		var shortcuts = files.Where(f => f.Flags.Contains(InventurDetection.ShortcutFlag))
			.Select(f => new InventurShortcut(f.Path, f.LinkTarget))
			.OrderBy(s => s.Path, StringComparer.Ordinal)
			.ToList();

		return new InventurRun(
			fullRoot,
			new InventurTotals(
				files.Count,
				files.Sum(f => f.Bytes),
				directories.Count,
				emptyFolders),
			ByType(files),
			LargestFiles(files),
			CollectDuplicates(files),
			shortcuts,
			PdfTotals(files),
			MediaTotals(files),
			EstimateReadingCost(files, model, settings),
			files);
	}

	/// <summary>The walk's relative path convention: '/'-separated, relative to the walked root.</summary>
	private static string Relative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

	private static byte[] ReadHeader(Stream stream)
	{
		var header = new byte[Math.Min(InventurDetection.HeaderBytes, stream.Length)];
		stream.ReadExactly(header, 0, header.Length);
		return header;
	}

	private static string? ReadShortcutLinkTarget(string path)
	{
		try
		{
			return InventurDetection.ShortcutLinkTarget(File.ReadAllText(path, System.Text.Encoding.UTF8));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// The two deterministic PDF probes: pages through the reading job's
	/// PDFium renderer, extractable text through the extraction job's bounded
	/// PdfPig reader — the exact code paths a stored Noten-Datei would take.
	/// Both run raw over the source bytes; a failure is a recorded fact with
	/// its German reason, never a walk crash.
	/// </summary>
	private static InventurPdfFacts AnalyzePdf(string path)
	{
		int? pages = null;
		string? pageCountReason = null;
		try
		{
			pages = new PdfiumPageRenderer().PageCount(File.ReadAllBytes(path));
		}
		catch (UnreadablePdfException exception)
		{
			pageCountReason = exception.Message;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			pageCountReason = "Die Datei konnte nicht gelesen werden.";
		}

		string text;
		string? reason;
		try
		{
			using var stream = File.OpenRead(path);
			var outcome = PdfTextExtractor.Extract(stream, new ExtractionOptions(), CancellationToken.None);
			text = outcome.Text is not null ? "embeddedText" : outcome.NoText ? "scan" : "failed";
			reason = outcome.FailureReason;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			text = "failed";
			reason = "Die Datei konnte nicht gelesen werden.";
		}

		return new InventurPdfFacts(pages, text, reason ?? pageCountReason);
	}

	private static IReadOnlyList<InventurDuplicate> CollectDuplicates(List<InventurFileEntry> files) =>
	[.. files
		.GroupBy(f => new InventurNameKey(
			Path.GetDirectoryName(f.Path),
			InventurDetection.DuplicateStem(Path.GetFileName(f.Path)).ToLowerInvariant(),
			Path.GetExtension(f.Path).ToLowerInvariant()))
		.Where(g => g.Count() > 1)
		.Select(g =>
		{
			// The key folds case for grouping; the report keeps the first
			// member's original spelling.
			var first = g.First();
			return new InventurDuplicate(
				Path.GetDirectoryName(first.Path) ?? "",
				InventurDetection.DuplicateStem(Path.GetFileName(first.Path)),
				Path.GetExtension(first.Path),
				[.. g.Select(f => f.Path).Order(StringComparer.Ordinal)],
				[.. g.Select(f => f.Bytes).Order()]);
		})
		.OrderBy(d => d.Folder, StringComparer.Ordinal)
		.ThenBy(d => d.Stem, StringComparer.Ordinal)
		.ThenBy(d => d.Extension, StringComparer.Ordinal)];

	private static IReadOnlyList<InventurByType> ByType(List<InventurFileEntry> files) =>
		[.. files
			.GroupBy(f => f.ContentType)
			.Select(g => new InventurByType(g.Key, g.Count(), g.Sum(f => f.Bytes)))
			.OrderByDescending(b => b.ContentType is null)
			.ThenBy(b => b.ContentType, StringComparer.Ordinal)];

	private static IReadOnlyList<InventurLargest> LargestFiles(List<InventurFileEntry> files) =>
		[.. files
			.OrderByDescending(f => f.Bytes)
			.ThenBy(f => f.Path, StringComparer.Ordinal)
			.Take(LargestLimit)
			.Select(f => new InventurLargest(f.Path, f.Bytes))];

	/// <summary>Per-PDF totals over the classification outcomes of the walk.</summary>
	private static InventurPdfTotals PdfTotals(List<InventurFileEntry> files)
	{
		var pdfs = files.Where(f => f.Pdf is not null).Select(f => (File: f, Facts: f.Pdf!)).ToList();
		return new InventurPdfTotals(
			pdfs.Count,
			pdfs.Count(p => p.Facts.Text == "embeddedText"),
			pdfs.Count(p => p.Facts.Text == "scan"),
			pdfs.Where(p => p.Facts.Text == "scan").Sum(p => p.Facts.Pages ?? 0),
			[.. pdfs
				.Where(p => p.Facts.Text! == "failed")
				.Select(p => new InventurPdfFailure(p.File.Path, p.Facts.Reason ?? "Das Dokument konnte nicht gelesen werden."))
				.OrderBy(x => x.Path, StringComparer.Ordinal)]);
	}

	/// <summary>MIDI presence plus the playback copy need (#171): playable containers are ready, the rest needs a conversion.</summary>
	private static InventurMediaTotals MediaTotals(List<InventurFileEntry> files)
	{
		var media = files.Where(f => f.Playable is not null).ToList();
		return new InventurMediaTotals(
			files.Count(f => f.Flags.Contains(InventurDetection.MidiFlag)),
			media.Count,
			media.Count(f => f.Playable is true),
			media.Count(f => f.Playable is false));
	}

	/// <summary>
	/// The analytical scan-reading estimate (architecture §14): each scanned
	/// document is bounded by the reading job's own MaxPagesPerRequest and its
	/// pages are pooled into ImagesPerCall image calls, where a call reserves
	/// the transcription prompt plus the worst-case image tokens and its own
	/// output bound. Prices come analytically from <c>Archive:Ai:Models</c>;
	/// nothing calls a model and no ledger row appears.
	/// </summary>
	private static InventurReadingCost EstimateReadingCost(List<InventurFileEntry> files, string model, InventurSettings settings)
	{
		var reading = settings.Reading;
		var scanned = files
			.Where(f => f.Pdf is { Text: "scan" })
			.Select(f => f.Pdf!.Pages ?? 0)
			.ToList();
		var calls = scanned.Sum(pages =>
		{
			var read = Math.Min(pages, reading.MaxPagesPerRequest);
			return (read + reading.ImagesPerCall - 1) / reading.ImagesPerCall;
		});
		var inputTokens = calls * ((long)reading.ImagesPerCall * reading.EstimatedTokensPerImage + PromptTokensPerCall);
		var outputTokens = calls * (long)reading.TranscriptionMaxOutputTokens;
		var price = settings.Ai.FindPrice(model);
		decimal? eur = price is null
			? null
			: inputTokens * price.InputPricePerMillionEur / 1_000_000m
				+ outputTokens * price.OutputPricePerMillionEur / 1_000_000m;
		return new InventurReadingCost(
			model,
			price?.InputPricePerMillionEur,
			price?.OutputPricePerMillionEur,
			scanned.Count,
			scanned.Sum(pages => Math.Min(pages, reading.MaxPagesPerRequest)),
			calls,
			inputTokens,
			outputTokens,
			eur,
			price is null
				? $"Kein Preis für das Modell '{model}' unter Archive:Ai:Models konfiguriert; die Schätzung bleibt ohne EUR-Betrag."
				: null,
			new InventurReadingCostAssumptions(
				reading.MaxPagesPerRequest,
				reading.ImagesPerCall,
				reading.EstimatedTokensPerImage,
				PromptTokensPerCall,
				reading.TranscriptionMaxOutputTokens));
	}
}
