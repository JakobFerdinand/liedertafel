using Archive.Backend.Reading;
using SkiaSharp;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034-1: the PDFium page renderer of the AI reading job. Real PDFium and
/// Skia run here (the packages ship their native libraries), so these tests
/// prove the job can render in the production image's runtime.
/// </summary>
public sealed class PdfPageRendererTests
{
	private readonly PdfiumPageRenderer renderer = new();

	[Fact]
	public void CountsThePagesOfADocument()
	{
		Assert.Equal(3, renderer.PageCount(ScanFixtures.TextPdf("Eins", "Zwei", "Drei")));
	}

	[Fact]
	public void RendersTheRequestedPagesAsJpegWithinTheResolutionBound()
	{
		var pdf = ScanFixtures.ScanPdf(["Seite eins"], ["Seite zwei"], ["Seite drei"]);

		var pages = renderer.Render(pdf, firstPage: 2, count: 2, maxLongSide: 1000, jpegQuality: 80, CancellationToken.None);

		Assert.Equal([2, 3], pages.Select(p => p.PageNumber));
		foreach (var page in pages)
		{
			Assert.Equal("image/jpeg", page.ContentType);
			using var bitmap = SKBitmap.Decode(page.Bytes);
			// A4 portrait: the long side is the height, scaled to the bound.
			Assert.Equal(1000, bitmap.Height);
			Assert.InRange(bitmap.Width, 700, 720);
		}
	}

	[Fact]
	public void RenderedScanPagesCarryTheDrawnContent()
	{
		var pdf = ScanFixtures.ScanPdf(["Waldfahrt"]);

		var page = Assert.Single(renderer.Render(pdf, 1, 1, 800, 80, CancellationToken.None));

		using var bitmap = SKBitmap.Decode(page.Bytes);
		var dark = 0;
		for (var y = 0; y < bitmap.Height; y += 2)
			for (var x = 0; x < bitmap.Width; x += 2)
				if (bitmap.GetPixel(x, y).Red < 100)
					dark++;
		// A blank white page would have none; the drawn words have many.
		Assert.True(dark > 100, $"Only {dark} dark pixels were rendered.");
	}

	[Fact]
	public void ClampsTheRangeToThePagesThatExist()
	{
		var pdf = ScanFixtures.TextPdf("Eins", "Zwei");

		var pages = renderer.Render(pdf, firstPage: 2, count: 10, maxLongSide: 600, jpegQuality: 80, CancellationToken.None);

		Assert.Equal([2], pages.Select(p => p.PageNumber));
		Assert.Empty(renderer.Render(pdf, firstPage: 3, count: 10, maxLongSide: 600, jpegQuality: 80, CancellationToken.None));
	}

	[Fact]
	public void AnUnreadableDocumentIsADeterministicFailure()
	{
		var corrupt = "%PDF-1.7\n%%EOF\n"u8.ToArray();

		Assert.Throws<UnreadablePdfException>(() => renderer.PageCount(corrupt));
		Assert.Throws<UnreadablePdfException>(() => renderer.Render(corrupt, 1, 1, 600, 80, CancellationToken.None));
	}

	[Fact]
	public void CancellationStopsBetweenPages()
	{
		var pdf = ScanFixtures.TextPdf("Eins", "Zwei");
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();

		Assert.Throws<OperationCanceledException>(() => renderer.Render(pdf, 1, 2, 600, 80, cancelled.Token));
	}
}
