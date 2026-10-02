using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Fonts;
using Core = UglyToad.PdfPig.Core;

namespace Archive.Backend.Extraction;

/// <summary>
/// Result of one bounded PDF text extraction (ARC-034): either the
/// accumulated text, the explicit no-text result for scanned documents (a
/// useful terminal result, not an error), or a deterministic failure with a
/// German reason. Infrastructure/IO failures are never converted into an
/// outcome — they propagate so the worker takes its transient path.
/// </summary>
public sealed record ExtractionOutcome(string? Text, bool NoText, string? FailureReason)
{
	/// <summary>Extracted text within the configured bounds.</summary>
	public static ExtractionOutcome TextResult(string text) => new(text, NoText: false, FailureReason: null);

	/// <summary>The document parses but contains no extractable text (scanned PDF).</summary>
	public static ExtractionOutcome NoTextOutcome() => new(Text: null, NoText: true, FailureReason: null);

	/// <summary>Deterministic failure of the stored bytes; retrying cannot succeed.</summary>
	public static ExtractionOutcome Failure(string reason) => new(Text: null, NoText: false, FailureReason: reason);
}

/// <summary>
/// Pure bounded embedded-text extraction over PdfPig (ARC-034). Embedded text
/// only — no OCR is introduced. Document-structure problems of the stored
/// bytes map to deterministic <see cref="ExtractionOutcome.Failure"/> results
/// while infrastructure/IO/stream exceptions propagate untouched.
/// </summary>
public static class PdfTextExtractor
{
	public const string EncryptedReason = "Das Dokument ist verschlüsselt.";

	public const string CorruptReason = "Das Dokument konnte nicht gelesen werden.";

	/// <summary>
	/// Extracts the embedded text of one PDF within <paramref name="options"/>'s
	/// bounds. Pages are appended in order, separated by newlines. Reaching
	/// <see cref="ExtractionOptions.MaxPdfPages"/> or exceeding
	/// <see cref="ExtractionOptions.MaxTextCharacters"/> stops appending and
	/// hard-truncates — the bounds yield a truncated result, never a failure.
	/// Zero extracted characters answer the explicit no-text outcome (scanned
	/// PDF). <paramref name="token"/> is checked per page; cancellation
	/// propagates. IO/stream failures propagate for the worker's transient path.
	/// </summary>
	public static ExtractionOutcome Extract(Stream pdfStream, ExtractionOptions options, CancellationToken token)
	{
		if (!pdfStream.CanSeek)
		{
			// Blob streams are seekable; stay defensive for other callers.
			var buffered = new MemoryStream();
			pdfStream.CopyTo(buffered);
			buffered.Position = 0;
			pdfStream = buffered;
		}
		try
		{
			using var document = PdfDocument.Open(pdfStream);
			var text = new StringBuilder();
			var page = 0;
			foreach (var content in document.GetPages())
			{
				token.ThrowIfCancellationRequested();
				// The page cap stops appending (not a failure) and once the
				// text budget is spent whole-page extraction is skipped.
				if (page >= options.MaxPdfPages || text.Length >= options.MaxTextCharacters)
					break;
				if (text.Length > 0)
					text.Append('\n');
				text.Append(ContentOrderTextExtractor.GetText(content));
				if (text.Length > options.MaxTextCharacters)
					text.Length = options.MaxTextCharacters;
				page++;
			}
			return text.Length == 0
				? ExtractionOutcome.NoTextOutcome()
				: ExtractionOutcome.TextResult(text.ToString());
		}
		catch (Exception exception) when (exception is not OperationCanceledException && IsStructureException(exception))
		{
			return exception is PdfDocumentEncryptedException
				? ExtractionOutcome.Failure(EncryptedReason)
				: ExtractionOutcome.Failure(CorruptReason);
		}
	}

	/// <summary>
	/// PdfPig 0.1.16's document-structure exception types: these describe the
	/// stored bytes deterministically, so retrying the same object can never
	/// succeed. IO/network failures use different types and stay unmatched.
	/// </summary>
	private static bool IsStructureException(Exception exception) =>
		exception is Core.PdfDocumentFormatException
			or Core.PdfDocumentStackDepthException
			or PdfDocumentEncryptedException
			or CorruptCompressedDataException
			or InvalidFontFormatException
		|| (exception.InnerException is { } inner && IsStructureException(inner));
}
