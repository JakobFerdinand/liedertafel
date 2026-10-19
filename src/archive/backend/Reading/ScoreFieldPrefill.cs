using System.Text.RegularExpressions;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Lieder;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Reading;

/// <summary>
/// What the field step did so far. The caller owns the instance, so the
/// counts survive a step that fails halfway (regex writes already happened).
/// </summary>
public sealed class PrefillProgress
{
	public int Applied { get; set; }
}

/// <summary>
/// Step two of scan reading (ARC-034-1): fills the still-empty fields
/// of a Lied (Komponist, Textdichter, Bearbeiter, Tonart, Liedtext) from the text of one of
/// its Noten. Deterministic first: the
/// <see cref="ScoreTextAnalyzer"/> facts are written with source regex; only
/// when fields are still empty does the field agent run. Everything the
/// agent says is data that code verifies — a field is written only when it
/// is certain ("sicher") and its quote really stands in the text; everything
/// else is dropped. Writes go through the shared
/// <see cref="AutomatedFieldWriter"/> only, for fields that are still empty,
/// and only while the Datei is not in the trash. A value an
/// editor entered is never overwritten, and a new Lied is never
/// created here.
/// </summary>
public sealed class ScoreFieldPrefill(
	ArchiveDbContext db,
	AutomatedFieldWriter writer,
	ScanReadingModel model)
{
	/// <summary>Longest value a field read by the agent may have; longer values are skipped, not cut.</summary>
	private const int MaxNameLength = 150;

	private const int MaxLyricsLength = 3500;

	private const int MinQuoteLength = 4;

	private const int MinLyricsQuoteLength = 10;

	private static readonly string[] FieldOrder =
		["composer", "lyricist", "arranger", "musical_key", "lyrics"];

	public async Task RunAsync(
		Datei datei, string text, Guid? accountId, PrefillProgress progress, CancellationToken token)
	{
		// Only a Noten Datei that is still in the archive fills its Lied: a
		// Datei in the trash keeps its text but never writes fields.
		if (datei.LiedId is not { } liedId || datei.DeletedAt is not null || string.IsNullOrWhiteSpace(text))
			return;
		var lied = await db.Lieder.FirstOrDefaultAsync(l => l.Id == liedId, token);
		if (lied is null)
			return;

		var targets = new Dictionary<string, Target>(StringComparer.Ordinal)
		{
			["composer"] = new(FieldCatalog.EntityTypeLied, lied.Id, lied.Composer),
			["lyricist"] = new(FieldCatalog.EntityTypeLied, lied.Id, lied.Lyricist),
			["lyrics"] = new(FieldCatalog.EntityTypeLied, lied.Id, lied.Lyrics),
			["arranger"] = new(FieldCatalog.EntityTypeLied, lied.Id, lied.Arranger),
			["musical_key"] = new(FieldCatalog.EntityTypeLied, lied.Id, lied.MusicalKey),
		};
		var open = targets
			.Where(pair => string.IsNullOrWhiteSpace(pair.Value.Current))
			.Select(pair => pair.Key)
			.ToHashSet(StringComparer.Ordinal);
		if (open.Count == 0)
			return;

		// 1. Deterministic: what the analyzer reads, written as regex facts.
		var facts = ScoreTextAnalyzer.Analyze(text).Facts;
		foreach (var (field, value) in RegexFacts(facts))
		{
			if (!open.Contains(field))
				continue;
			var target = targets[field];
			var outcome = await writer.WriteFieldAsync(
				new AutomatedFieldWriter.FieldWriteRequest(target.EntityType, target.EntityId, field, value), accountId, token);
			open.Remove(field);
			Count(outcome, progress);
		}
		if (open.Count == 0)
			return;

		// 2. The agent, for the fields that are still empty.
		var wanted = FieldOrder.Where(open.Contains).ToList();
		var reading = await model.ReadFieldsAsync(text, wanted, accountId, token);
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var read in reading.Fields ?? [])
		{
			// Only requested fields, once each: whatever else the text made
			// the model say is ignored.
			if (read.Field is null || !open.Contains(read.Field) || !seen.Add(read.Field))
				continue;
			var value = CleanValue(read.Field, read.Value);
			if (value is null)
				continue;
			// Only a certain value whose quote stands in the text is written;
			// anything else is dropped.
			var certain = string.Equals(read.Certainty?.Trim(), "sicher", StringComparison.OrdinalIgnoreCase);
			if (!certain || !QuoteIsVerified(read, value, text))
				continue;
			var target = targets[read.Field];
			var outcome = await writer.WriteFieldAsync(
				new AutomatedFieldWriter.FieldWriteRequest(target.EntityType, target.EntityId, read.Field, value), accountId, token);
			Count(outcome, progress);
		}
	}

	private void Count(AutomatedFieldWriter.AutomatedWriteOutcome outcome, PrefillProgress progress)
	{
		if (outcome.Result == AutomatedFieldWriter.AutomatedWriteResult.Applied)
			progress.Applied++;
		else if (outcome.Result == AutomatedFieldWriter.AutomatedWriteResult.Refused)
			ForgetRefusedWrite();
	}

	/// <summary>
	/// A refused write (a lost save race, say) leaves its half-made change in
	/// the tracker; the next save would throw on it again and the run would
	/// pay for the field call twice. Drop the abandoned edits so the next
	/// field starts from what is stored.
	/// </summary>
	private void ForgetRefusedWrite()
	{
		foreach (var entry in db.ChangeTracker.Entries().ToList())
		{
			if (entry.State == EntityState.Modified
				&& entry.Entity is Lied)
				entry.State = EntityState.Detached;
		}
	}

	private static IEnumerable<(string Field, string Value)> RegexFacts(ScoreFacts facts)
	{
		if (facts.Composer is { } composer)
			yield return ("composer", composer);
		if (facts.Lyricist is { } lyricist)
			yield return ("lyricist", lyricist);
		if (facts.Arranger is { } arranger)
			yield return ("arranger", arranger);
		if (facts.MusicalKey is { } key)
			yield return ("musical_key", key);
	}

	/// <summary>Trims the value and bounds it; null when it is empty or too long to be a plausible reading.</summary>
	internal static string? CleanValue(string field, string? raw)
	{
		var value = raw?.Replace("\r\n", "\n").Trim();
		if (string.IsNullOrEmpty(value))
			return null;
		return field == "lyrics"
			? value.Length <= MaxLyricsLength ? value : null
			: value.Length <= MaxNameLength && !value.Contains('\n') ? value : null;
	}

	/// <summary>
	/// The quote must stand in the text (ignoring case, line breaks and
	/// repeated blanks), and the quote must tie the value to the text: a name
	/// stands in its quote; every line of the lyrics stands in the text (a
	/// real first line cannot vouch for an invented stanza); the voice's
	/// first word and the key stand in their quote.
	/// </summary>
	public static bool QuoteIsVerified(ReadField read, string value, string text)
	{
		var normalizedText = Normalize(text);
		var quote = Normalize(read.Quote);
		var minimum = read.Field == "lyrics" ? MinLyricsQuoteLength : MinQuoteLength;
		if (quote.Length < minimum || !normalizedText.Contains(quote, StringComparison.Ordinal))
			return false;
		switch (read.Field)
		{
			case "title" or "composer" or "lyricist" or "arranger":
				return quote.Contains(Normalize(value), StringComparison.Ordinal);
			case "lyrics":
				return value.Replace("\r\n", "\n").Split('\n').Select(Normalize)
					.Where(line => line.Length > 0)
					.All(line => normalizedText.Contains(line, StringComparison.Ordinal));
			case "musical_key":
				var key = Compact(value);
				return key.Length > 0 && Compact(quote).Contains(key, StringComparison.Ordinal);
			default:
				return true;
		}
	}

	private static string Compact(string value) => Regex.Replace(Normalize(value), @"[^\p{L}\p{N}]", string.Empty);

	private static string Normalize(string? value)
		=> string.IsNullOrWhiteSpace(value)
			? string.Empty
			: Regex.Replace(value.Replace("­", ""), @"\s+", " ").Trim().ToLowerInvariant();

	private sealed record Target(string EntityType, Guid EntityId, string? Current);
}
