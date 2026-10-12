// The package marks its API per platform; the archive runs on Linux (the image) and
// Windows/macOS (developer machines), all supported.
#pragma warning disable CA1416
using PDFtoImage;
using SkiaSharp;

namespace Archive.Backend.Reading;

/// <summary>One rendered page, ready to be sent to the model as an image.</summary>
public sealed record RenderedPage(int PageNumber, byte[] Bytes, string ContentType);

/// <summary>The stored bytes are not a PDF the renderer can open: deterministic, a retry cannot help.</summary>
public sealed class UnreadablePdfException(Exception? inner = null)
	: Exception("The PDF could not be opened for rendering.", inner);

/// <summary>
/// Renders PDF pages to bounded images inside the AI reading job (ARC-034-1).
/// The seam exists so the worker tests need no native library; the production
/// implementation is <see cref="PdfiumPageRenderer"/>.
/// </summary>
public interface IPdfPageRenderer
{
	int PageCount(byte[] pdf);

	/// <summary>
	/// Renders up to <paramref name="count"/> pages starting at the one-based
	/// <paramref name="firstPage"/>, clamped to the pages that exist. The longer
	/// side of each image is exactly <paramref name="maxLongSide"/> pixels.
	/// </summary>
	IReadOnlyList<RenderedPage> Render(byte[] pdf, int firstPage, int count, int maxLongSide, int jpegQuality, CancellationToken token);
}

/// <summary>
/// PDFium through the PDFtoImage package, which ships the native PDFium and
/// Skia libraries for the platforms the archive runs on (no apt packages or
/// system fonts are needed in the image). Encrypted or corrupt documents are
/// reported as <see cref="UnreadablePdfException"/>; the page budget and the
/// resolution are the caller's bounds, so memory stays proportional to one
/// page at a time.
/// </summary>
public sealed class PdfiumPageRenderer : IPdfPageRenderer
{
	public int PageCount(byte[] pdf)
	{
		try
		{
			return Conversion.GetPageCount(pdf);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			throw new UnreadablePdfException(exception);
		}
	}

	public IReadOnlyList<RenderedPage> Render(
		byte[] pdf, int firstPage, int count, int maxLongSide, int jpegQuality, CancellationToken token)
	{
		var total = PageCount(pdf);
		var last = Math.Min(total, firstPage + count - 1);
		var pages = new List<RenderedPage>();
		for (var number = Math.Max(1, firstPage); number <= last; number++)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				var size = Conversion.GetPageSize(pdf, number - 1);
				var landscape = size.Width > size.Height;
				var options = new RenderOptions(
					Width: landscape ? maxLongSide : null,
					Height: landscape ? null : maxLongSide,
					WithAspectRatio: true,
					BackgroundColor: SKColors.White);
				using var bitmap = Conversion.ToImage(pdf, number - 1, password: null, options: options);
				using var image = SKImage.FromBitmap(bitmap);
				using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);
				pages.Add(new RenderedPage(number, jpeg.ToArray(), "image/jpeg"));
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				throw new UnreadablePdfException(exception);
			}
		}
		return pages;
	}
}
