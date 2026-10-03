using Archive.Backend.Extraction;

namespace Archive.Backend.Tests;

public sealed class ScoreTextAnalyzerTests
{
	/// <summary>Text as PdfPig extracts it from a notation-program voice part.</summary>
	private const string TenorPart = "Tenor 1 A♭\n44 .å\nHo -\nåê\nsi -\nå\nan -\nå\nna,\n.å\nHo -\nåê\nsi -\nå\nan -\nå\nna,";

	[Fact]
	public void VoicePartYieldsVoiceKeyTimeSignatureAndReadableLyrics()
	{
		var analysis = ScoreTextAnalyzer.Analyze(TenorPart);

		Assert.Equal("Tenor 1", analysis.Facts.Voice);
		Assert.Equal("A♭", analysis.Facts.MusicalKey);
		Assert.Equal("4/4", analysis.Facts.TimeSignature);
		Assert.Equal("Tenor 1 A♭ 44 Hosianna, Hosianna,", analysis.CleanText);
	}

	[Theory]
	[InlineData("1. Tenor\nFreude", "Tenor 1")]
	[InlineData("II. Bass\nFreude", "Bass 2")]
	[InlineData("Bass II\nFreude", "Bass 2")]
	[InlineData("BARITON\nFreude", "Bariton")]
	[InlineData("Tenor 1\nTenor 2\nFreude", "Tenor 1+2")]
	[InlineData("Lied\nTenor 1\nla\nTenor 2\nla\nBass 1\nla\nBass 2\nla", ScoreTextAnalyzer.FullScoreLabel)]
	[InlineData("Sopran\nAlt\nFreude", ScoreTextAnalyzer.FullScoreLabel)]
	public void VoiceIsReadFromHeadingAndStaffLabels(string text, string expected)
		=> Assert.Equal(expected, ScoreTextAnalyzer.Analyze(text).Facts.Voice);

	[Fact]
	public void VoiceWordsInsideTheLyricsAreNotVoices()
	{
		// Far beyond the heading: "Alt" is the German word and "Bass" is part of a sentence.
		var text = "Abendlied\n" + string.Join('\n', Enumerable.Repeat("la la la la", 20))
			+ "\nAlt\nund Jung singt der Bass im Chor";

		Assert.Null(ScoreTextAnalyzer.Analyze(text).Facts.Voice);
	}

	[Theory]
	[InlineData("Abendlied in As-Dur", "As-Dur")]
	[InlineData("Abendlied d moll", "d-Moll")]
	[InlineData("Evening Song\nBb major", "B♭-Dur")]
	[InlineData("Evening Song\nF# minor", "f♯-Moll")]
	[InlineData("Bass 2 F#\n34", "F♯")]
	public void KeyIsReadFromWrittenNameOrAccidental(string text, string expected)
		=> Assert.Equal(expected, ScoreTextAnalyzer.Analyze(text).Facts.MusicalKey);

	[Theory]
	[InlineData("Bass 1\n3/4\nla", "3/4")]
	[InlineData("Bass 1\n68\nla", "6/8")]
	[InlineData("Bass 1\n4\n4\nla", "4/4")]
	public void TimeSignatureIsReadFromFractionOrStackedDigits(string text, string expected)
		=> Assert.Equal(expected, ScoreTextAnalyzer.Analyze(text).Facts.TimeSignature);

	[Fact]
	public void NumbersOutsideTheHeadingAreNotTimeSignatures()
	{
		var text = "Abendlied\n" + string.Join('\n', Enumerable.Repeat("la la la la", 20)) + "\n44";

		var facts = ScoreTextAnalyzer.Analyze(text).Facts;

		Assert.Null(facts.TimeSignature);
		Assert.Null(facts.MusicalKey);
	}

	[Fact]
	public void CreditsTempoAndCopyrightAreRead()
	{
		var text = "Abendlied\nText: Matthias Claudius   Musik: J. A. P. Schulz\nSatz:\nFranz Huber\n"
			+ "Andante å = 72\nDer Mond ist auf - ge - gan - gen\n© 1998 Musikverlag Beispiel";

		var analysis = ScoreTextAnalyzer.Analyze(text);

		Assert.Equal("Matthias Claudius", analysis.Facts.Lyricist);
		Assert.Equal("J. A. P. Schulz", analysis.Facts.Composer);
		Assert.Equal("Franz Huber", analysis.Facts.Arranger);
		Assert.Equal("Andante, 72 Schläge/min", analysis.Facts.Tempo);
		Assert.Equal("© 1998 Musikverlag Beispiel", analysis.Facts.Copyright);
		Assert.Contains("Der Mond ist aufgegangen", analysis.CleanText);
	}

	[Fact]
	public void SharedCreditNamesComposerAndLyricist()
	{
		var facts = ScoreTextAnalyzer.Analyze("Lied\nWorte und Weise: Hans Baumann").Facts;

		Assert.Equal("Hans Baumann", facts.Composer);
		Assert.Equal("Hans Baumann", facts.Lyricist);
	}

	[Fact]
	public void DashBetweenCapitalisedWordsIsKept()
		=> Assert.Equal("Wien - Graz", ScoreTextAnalyzer.Analyze("Wien - Graz").CleanText);

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("å åê .å")]
	public void TextWithoutReadableContentYieldsNothing(string? text)
	{
		var analysis = ScoreTextAnalyzer.Analyze(text);

		Assert.Equal(string.Empty, analysis.CleanText);
		Assert.True(analysis.Facts.IsEmpty);
	}
}
