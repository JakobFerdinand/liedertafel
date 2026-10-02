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

	public const string TooLargeReason = "Das Dokument ist zu groß für die automatische Textauswertung.";

	public const string TooMuchTextReason = "Das Dokument enthält zu viel Text für die automatische Auswertung.";

	/// <summary>
	/// Extracts the embedded text of one PDF within <paramref name="options"/>'s
	/// bounds. Pages are appended in order, separated by newlines. Pages beyond
	/// <see cref="ExtractionOptions.MaxPdfPages"/> are never parsed; partial text
	/// completes. Text exceeding <see cref="ExtractionOptions.MaxTextCharacters"/>
	/// fails deterministically; buffering and appending never exceed their caps.
	/// Zero extracted characters answer the explicit no-text outcome (scanned
	/// PDF). Cancellation is checked before opening, per page and before returning.
	/// PdfPig's synchronous document/page parsing cannot be interrupted internally:
	/// one page can overrun the deadline or expand beyond the compressed byte cap.
	/// Expired outcomes are rejected; decoded expansion relies on container memory
	/// limits and the worker's stale-lease recovery. IO/stream failures propagate.
	/// </summary>
	public static ExtractionOutcome Extract(Stream pdfStream, ExtractionOptions options, CancellationToken token)
	{
		using var buffered = pdfStream.CanSeek ? null : new MemoryStream();
		try
		{
			token.ThrowIfCancellationRequested();
			if (buffered is not null)
			{
				var buffer = new byte[8192];
				while (buffered.Length < options.MaxPdfBytes)
				{
					token.ThrowIfCancellationRequested();
					var read = pdfStream.Read(buffer, 0, (int)Math.Min(buffer.Length, options.MaxPdfBytes - buffered.Length));
					if (read == 0)
						break;
					buffered.Write(buffer, 0, read);
				}
				token.ThrowIfCancellationRequested();
				// One probe byte detects overflow; it is never buffered.
				if (buffered.Length == options.MaxPdfBytes && pdfStream.ReadByte() != -1)
				{
					token.ThrowIfCancellationRequested();
					return ExtractionOutcome.Failure(TooLargeReason);
				}
				buffered.Position = 0;
				pdfStream = buffered;
			}
			token.ThrowIfCancellationRequested();
			using var document = PdfDocument.Open(pdfStream);
			var text = new StringBuilder();
			for (var index = 0; index < document.NumberOfPages; index++)
			{
				token.ThrowIfCancellationRequested();
				if (index >= options.MaxPdfPages)
					break;
				var content = document.GetPage(index + 1);
				var pageText = ContentOrderTextExtractor.GetText(content);
				token.ThrowIfCancellationRequested();
				var separator = text.Length > 0 ? 1 : 0;
				var remaining = options.MaxTextCharacters - text.Length;
				var overlength = (long)pageText.Length + separator > remaining;
				if (separator > 0 && remaining > 0)
				{
					text.Append('\n');
					remaining--;
				}
				text.Append(pageText, 0, Math.Min(pageText.Length, remaining));
				if (overlength)
					return ExtractionOutcome.Failure(TooMuchTextReason);
			}
			token.ThrowIfCancellationRequested();
			return text.Length == 0
				? ExtractionOutcome.NoTextOutcome()
				: ExtractionOutcome.TextResult(text.ToString());
		}
		catch (Exception exception) when (exception is not OperationCanceledException && IsStructureException(exception))
		{
			token.ThrowIfCancellationRequested();
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
