#pragma warning disable CA1416
using System.Text.RegularExpressions;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Archive.Backend.Reading;

/// <summary>One suggestion for a field of the Auftritt form; <see cref="Sure"/> is false when the model was unsure.</summary>
public sealed record ProgrammFeld(string Field, string Value, bool Sure);

/// <summary>One entry of the scanned programme with what the catalogue says about its title.</summary>
public sealed record ProgrammEintrag(string Text, bool Sure, ProgrammMatch Match);

/// <summary>The whole reading of a programme scan: form suggestions and the matched Programm.</summary>
public sealed record ProgrammLesung(IReadOnlyList<ProgrammFeld> Felder, IReadOnlyList<ProgrammEintrag> Programm);

/// <summary>The file could not be read as a programme scan (not a PDF or photo, encrypted, corrupt, too large).</summary>
public sealed class ProgrammFileUnreadableException(string reason) : Exception(reason)
{
	public string Reason { get; } = reason;
}

/// <summary>
/// Reads one dropped programme scan or photo (#131) before any Auftritt
/// exists: the embedded text of a PDF first, a scan or photo through the
/// model, then title, date, place and the entries of the Programm. It writes
/// nothing: the answer is suggestions for the editor's form plus, per entry,
/// how it matches the catalogue (<see cref="ProgrammMatcher"/>). The checks are
/// the reading job's: a value needs a quote that really stands in the text,
/// otherwise it is dropped; a date needs a plausible year, month and day.
/// </summary>
public sealed class ProgrammReader(
	ScanReadingModel model,
	IPdfPageRenderer renderer,
	ArchiveDbContext db,
	IOptions<ReadingOptions> options,
	IOptions<ExtractionOptions> extractionOptions)
{
	/// <summary>Pages of a scanned programme that are transcribed: a programme is a leaflet, not a book.</summary>
	private const int ScanPages = 4;

	/// <summary>Most entries one programme may have; more is not a programme.</summary>
	public const int MaxEntries = 60;

	private const int MinQuoteLength = 3;

	private static readonly string[] MonthNames =
		["januar", "februar", "marz", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "dezember"];

	private static readonly Regex Numbering = new(@"^\s*\d{1,3}\s*[.)]\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static bool IsPdf(ReadOnlySpan<byte> bytes) => bytes.StartsWith("%PDF-"u8);

	public static bool IsPhoto(ReadOnlySpan<byte> bytes)
		=> bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })
			|| bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
			|| (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8));

	public async Task<ProgrammLesung> ReadAsync(byte[] file, Guid? accountId, CancellationToken token)
	{
		if (file.Length > options.Value.MaxPdfBytes)
			throw new ProgrammFileUnreadableException(ReadingWorker.TooLargeReason);
		var text = IsPdf(file) ? await PdfTextAsync(file, accountId, token) : await PhotoTextAsync(file, accountId, token);
		if (string.IsNullOrWhiteSpace(text))
			return new ProgrammLesung([], []);

		var reading = await model.ReadProgrammAsync(text, accountId, token);
		var normalizedText = ProgrammMatcher.Normalize(text);
		var felder = Header(reading.Fields ?? [], normalizedText);

		var lieder = await db.Lieder.AsNoTracking().Select(l => new MatchableLied(l.Id, l.Title)).ToListAsync(token);
		var programm = new List<ProgrammEintrag>();
		foreach (var punkt in reading.Punkte ?? [])
		{
			if (programm.Count >= MaxEntries)
				break;
			var title = CleanTitle(punkt.Title);
			if (title is null || !QuoteStands(punkt.Quote, title, normalizedText))
				continue;
			programm.Add(new ProgrammEintrag(title, IsSure(punkt.Certainty), ProgrammMatcher.Match(title, lieder)));
		}
		return new ProgrammLesung(felder, programm);
	}

	private async Task<string?> PdfTextAsync(byte[] pdf, Guid? accountId, CancellationToken token)
	{
		ExtractionOutcome outcome;
		using (var stream = new MemoryStream(pdf, writable: false))
			outcome = PdfTextExtractor.Extract(stream, extractionOptions.Value, token);
		if (outcome.FailureReason is { } reason)
			throw new ProgrammFileUnreadableException(reason);
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
			return pages.Count == 0 ? null : await model.TranscribeProgrammAsync(pages, accountId, token);
		}
		catch (UnreadablePdfException)
		{
			throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
		}
	}

	private async Task<string?> PhotoTextAsync(byte[] photo, Guid? accountId, CancellationToken token)
	{
		if (!IsPhoto(photo))
			throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
		var page = Downscale(photo, options.Value.MaxImageLongSide, options.Value.JpegQuality);
		return await model.TranscribeProgrammAsync([page], accountId, token);
	}

	/// <summary>
	/// A phone photo is turned upright (EXIF) and bounded like a rendered PDF
	/// page, so the model call costs what a page costs.
	/// </summary>
	private static RenderedPage Downscale(byte[] photo, int maxLongSide, int jpegQuality)
	{
		try
		{
			using var data = SKData.CreateCopy(photo);
			using var codec = SKCodec.Create(data) ?? throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
			var origin = codec.EncodedOrigin;
			using var decoded = SKBitmap.Decode(codec) ?? throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
			using var upright = Rotate(decoded, origin);
			var scale = Math.Min(1.0, (double)maxLongSide / Math.Max(upright.Width, upright.Height));
			var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
			var height = Math.Max(1, (int)Math.Round(upright.Height * scale));
			using var resized = upright.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
				?? throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
			using var image = SKImage.FromBitmap(resized);
			using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
			return new RenderedPage(1, jpeg.ToArray(), "image/jpeg");
		}
		catch (Exception exception) when (exception is not (OperationCanceledException or ProgrammFileUnreadableException))
		{
			throw new ProgrammFileUnreadableException(ReadingWorker.UnreadableReason);
		}
	}

	private static SKBitmap Rotate(SKBitmap source, SKEncodedOrigin origin)
	{
		var degrees = origin switch
		{
			SKEncodedOrigin.RightTop => 90,
			SKEncodedOrigin.BottomRight => 180,
			SKEncodedOrigin.LeftBottom => 270,
			_ => 0,
		};
		if (degrees == 0)
			return source.Copy();
		var swap = degrees != 180;
		var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
		using var canvas = new SKCanvas(rotated);
		canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
		canvas.RotateDegrees(degrees);
		canvas.Translate(-source.Width / 2f, -source.Height / 2f);
		using var image = SKImage.FromBitmap(source);
		canvas.DrawImage(image, 0, 0);
		return rotated;
	}

	private static List<ProgrammFeld> Header(List<ReadField> read, string normalizedText)
	{
		var byName = new Dictionary<string, ReadField>(StringComparer.Ordinal);
		foreach (var field in read)
		{
			if (field.Field is { } name && !string.IsNullOrWhiteSpace(field.Value))
				byName.TryAdd(name, field);
		}
		var found = new List<ProgrammFeld>();

		if (byName.TryGetValue("title", out var title) && Single(title.Value, EventEndpoints.TitleMaxLength) is { } titleValue
			&& QuoteStands(title.Quote, titleValue, normalizedText))
			found.Add(new ProgrammFeld("title", titleValue, IsSure(title.Certainty)));

		if (byName.TryGetValue("kind", out var kind) && kind.Value!.Trim().ToLowerInvariant() is var kindValue
			&& EventKinds.Known.Contains(kindValue) && QuoteStands(kind.Quote, null, normalizedText))
			found.Add(new ProgrammFeld("kind", kindValue, IsSure(kind.Certainty)));

		// A date is built from the outside in: a month needs a year, a day needs a month.
		if (byName.TryGetValue("year", out var yearRead) && int.TryParse(yearRead.Value!.Trim(), out var year)
			&& year is >= 1800 and <= 2100 && QuoteStands(yearRead.Quote, null, normalizedText)
			&& ProgrammMatcher.Normalize(yearRead.Quote).Contains(year.ToString(), StringComparison.Ordinal))
		{
			found.Add(new ProgrammFeld("jahr", year.ToString(), IsSure(yearRead.Certainty)));
			if (byName.TryGetValue("month", out var monthRead) && int.TryParse(monthRead.Value!.Trim(), out var month)
				&& month is >= 1 and <= 12 && QuoteStands(monthRead.Quote, null, normalizedText)
				&& MonthStands(monthRead.Quote!, month))
			{
				found.Add(new ProgrammFeld("monat", month.ToString(), IsSure(monthRead.Certainty)));
				if (byName.TryGetValue("day", out var dayRead) && int.TryParse(dayRead.Value!.Trim(), out var day)
					&& day >= 1 && day <= DateTime.DaysInMonth(year, month) && QuoteStands(dayRead.Quote, null, normalizedText)
					&& Regex.IsMatch(dayRead.Quote!, $@"(?<!\d){day}(?!\d)", RegexOptions.CultureInvariant))
					found.Add(new ProgrammFeld("tag", day.ToString(), IsSure(dayRead.Certainty)));
			}
			if (byName.TryGetValue("approximate", out var approximate)
				&& string.Equals(approximate.Value!.Trim(), "ja", StringComparison.OrdinalIgnoreCase)
				&& QuoteStands(approximate.Quote, null, normalizedText))
				found.Add(new ProgrammFeld("unsicher", "true", Sure: true));
		}

		if (byName.TryGetValue("venue", out var venue) && Single(venue.Value, EventEndpoints.VenueMaxLength) is { } venueValue
			&& QuoteStands(venue.Quote, venueValue, normalizedText))
			found.Add(new ProgrammFeld("venue", venueValue, IsSure(venue.Certainty)));
		return found;
	}

	private static bool MonthStands(string quote, int month)
	{
		var folded = ProgrammMatcher.Normalize(quote);
		if (folded.Contains(MonthNames[month - 1], StringComparison.Ordinal))
			return true;
		return Regex.IsMatch(quote, $@"(?<!\d)0?{month}\s*\.", RegexOptions.CultureInvariant);
	}

	/// <summary>One line, trimmed and bounded; longer or multi-line values are skipped, not cut.</summary>
	private static string? Single(string? raw, int maxLength)
	{
		var value = raw?.Trim();
		return string.IsNullOrEmpty(value) || value.Length > maxLength || value.Contains('\n') ? null : value;
	}

	private static string? CleanTitle(string? raw)
	{
		var value = Single(raw, EventEndpoints.TitleMaxLength);
		if (value is null)
			return null;
		value = Numbering.Replace(value, string.Empty).Trim();
		return value.Length == 0 ? null : value;
	}

	/// <summary>
	/// The quote must stand in the text (ignoring case, accents, punctuation
	/// and blanks), and where a value is given it must stand in its quote.
	/// </summary>
	private static bool QuoteStands(string? quote, string? value, string normalizedText)
	{
		var normalizedQuote = ProgrammMatcher.Normalize(quote);
		if (normalizedQuote.Length < MinQuoteLength || !normalizedText.Contains(normalizedQuote, StringComparison.Ordinal))
			return false;
		return value is null || normalizedQuote.Contains(ProgrammMatcher.Normalize(value), StringComparison.Ordinal);
	}

	private static bool IsSure(string? certainty)
		=> string.Equals(certainty?.Trim(), "sicher", StringComparison.OrdinalIgnoreCase);
}
