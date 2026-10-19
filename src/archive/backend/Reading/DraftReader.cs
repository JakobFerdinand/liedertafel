using Archive.Backend.Extraction;
using Microsoft.Extensions.Options;

namespace Archive.Backend.Reading;

/// <summary>One suggestion for a field of the draft form; <see cref="Sure"/> is false when the model was unsure.</summary>
public sealed record DraftSuggestion(string Field, string Value, bool Sure);

/// <summary>The PDF could not be read as a document (not a PDF, encrypted, corrupt, too large).</summary>
public sealed class DraftPdfUnreadableException(string reason) : Exception(reason)
{
	public string Reason { get; } = reason;
}

/// <summary>
/// Reads one PDF of a Lied draft (#177), before any Lied or Datei exists: the
/// embedded text first, a scan through the model, then the fields. It writes
/// nothing: the answer is a list of suggestions the editor's form may take
/// over. The checks are the reading job's: a value needs a quote that really
/// stands in the text, otherwise it is dropped. Wire names are the form's
/// field names, so a later persisted draft can store the answer as is.
/// </summary>
public sealed class DraftReader(
	ScanReadingModel model,
	IPdfPageRenderer renderer,
	IOptions<ReadingOptions> options,
	IOptions<ExtractionOptions> extractionOptions)
{
	/// <summary>Pages of a scan that are transcribed for a draft: title and credits stand at the front.</summary>
	private const int ScanPages = 3;

	private static readonly (string Model, string Form)[] Fields =
	[
		("title", "title"),
		("composer", "composer"),
		("arranger", "arranger"),
		("lyricist", "lyricist"),
		("voice_configuration", "voiceConfiguration"),
		("musical_key", "musicalKey"),
		("language", "language"),
		("lyrics", "lyrics"),
	];

	public async Task<IReadOnlyList<DraftSuggestion>> ReadAsync(byte[] pdf, Guid? accountId, CancellationToken token)
	{
		var text = await TextOfAsync(pdf, accountId, token);
		if (string.IsNullOrWhiteSpace(text))
			return [];

		var found = new Dictionary<string, DraftSuggestion>(StringComparer.Ordinal);
		// 1. What the analyzer reads deterministically is as sure as the text.
		var facts = ScoreTextAnalyzer.Analyze(text).Facts;
		foreach (var (field, value) in new[]
		{
			("composer", facts.Composer), ("lyricist", facts.Lyricist),
			("arranger", facts.Arranger), ("musical_key", facts.MusicalKey),
		})
		{
			if (value is not null)
				found[field] = new DraftSuggestion(Form(field), value, Sure: true);
		}

		// 2. The field agent for what is still open, checked like the job does.
		var wanted = Fields.Select(f => f.Model).Where(f => !found.ContainsKey(f)).ToList();
		if (wanted.Count > 0)
		{
			var reading = await model.ReadFieldsAsync(text, wanted, accountId, token);
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var read in reading.Fields ?? [])
			{
				if (read.Field is null || !wanted.Contains(read.Field) || !seen.Add(read.Field))
					continue;
				var value = ScoreFieldPrefill.CleanValue(read.Field, read.Value);
				if (value is null || !ScoreFieldPrefill.QuoteIsVerified(read, value, text))
					continue;
				var sure = string.Equals(read.Certainty?.Trim(), "sicher", StringComparison.OrdinalIgnoreCase);
				found[read.Field] = new DraftSuggestion(Form(read.Field), value, sure);
			}
		}
		return Fields.Where(f => found.ContainsKey(f.Model)).Select(f => found[f.Model]).ToList();
	}

	private async Task<string?> TextOfAsync(byte[] pdf, Guid? accountId, CancellationToken token)
	{
		if (pdf.Length > options.Value.MaxPdfBytes)
			throw new DraftPdfUnreadableException(ReadingWorker.TooLargeReason);
		ExtractionOutcome outcome;
		using (var stream = new MemoryStream(pdf, writable: false))
			outcome = PdfTextExtractor.Extract(stream, extractionOptions.Value, token);
		if (outcome.FailureReason is { } reason)
			throw new DraftPdfUnreadableException(reason);
		if (!outcome.NoText)
			return outcome.Text;

		// A scan has no embedded text: the model transcribes its first pages.
		try
		{
			var settings = options.Value;
			var count = Math.Min(ScanPages, Math.Min(settings.ImagesPerCall, renderer.PageCount(pdf)));
			if (count <= 0)
				return null;
			var pages = renderer.Render(pdf, 1, count, settings.MaxImageLongSide, settings.JpegQuality, token);
			return pages.Count == 0 ? null : await model.TranscribeAsync(pages, accountId, token);
		}
		catch (UnreadablePdfException)
		{
			throw new DraftPdfUnreadableException(ReadingWorker.UnreadableReason);
		}
	}

	private static string Form(string modelField) => Fields.First(f => f.Model == modelField).Form;
}
