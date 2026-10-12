using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-034-1: generated PDFs for the reading tests. A "scan" is a page that
/// holds only a JPEG (no text objects), like a real scanner output; the
/// picture really carries the drawn lines so a renderer test can see them.
/// </summary>
internal static class ScanFixtures
{
	/// <summary>A document of text pages (one string per page).</summary>
	public static byte[] TextPdf(params string[] pages)
	{
		var builder = new PdfDocumentBuilder();
		var font = builder.AddStandard14Font(Standard14Font.Helvetica);
		foreach (var text in pages)
			builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(50, 700), font);
		return builder.Build();
	}

	/// <summary>A scanned document: every page is one JPEG with one dark bar drawn per given line.</summary>
	public static byte[] ScanPdf(params string[][] pages)
	{
		var builder = new PdfDocumentBuilder();
		foreach (var lines in pages)
		{
			var page = builder.AddPage(PageSize.A4);
			page.AddJpeg(PageJpeg(lines), new PdfRectangle(0, 0, 595, 842));
		}
		return builder.Build();
	}

	public static byte[] PageJpeg(string[] lines)
	{
		using var bitmap = new SKBitmap(595, 842);
		using var canvas = new SKCanvas(bitmap);
		canvas.Clear(SKColors.White);
		// Bars stand for the lines of print: a build machine may have no fonts at all.
		using var paint = new SKPaint { Color = SKColors.Black };
		var y = 80f;
		foreach (var line in lines)
		{
			canvas.DrawRect(40, y, 20 + line.Length * 22, 24, paint);
			y += 48;
		}
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
		return data.ToArray();
	}
}
