using System.Text.Json;
using System.Text.RegularExpressions;
using Archive.Backend.Assets;
using Archive.Backend.Data;
using Archive.Backend.Extraction;
using Archive.Backend.Provenance;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Reading;

/// <summary>
/// What the field step did so far. The caller owns the instance, so the
/// counts survive a step that fails halfway (regex writes already happened).
/// </summary>
public sealed class PrefillProgress
{
	public int Applied { get; set; }

	public int Proposed { get; set; }
}

/// <summary>
/// Step two of scan reading (ARC-034-1): fills the still-empty catalogue
/// fields of a score from its text. Deterministic first: the
/// <see cref="ScoreTextAnalyzer"/> facts are written with source regex; only
/// when fields are still empty does the field agent run. Everything the
/// agent says is data that code verifies — a field is applied only when it
/// is certain ("sicher") and its quote really stands in the text; everything
/// else becomes a proposal. Writes go through the shared
/// <see cref="AutomatedFieldWriter"/> only, for fields that are empty and not
/// locked by a human, and only while the revision is the asset's current file.
/// A new song identity is never created here.
/// </summary>
public sealed class ScoreFieldPrefill(
	ArchiveDbContext db,
	AutomatedFieldWriter writer,
	ScanReadingModel model)
{
	public const string RegexReason = "Aus dem Text der Noten erkannt.";

	public const string UnverifiedReason = "Aus dem Scan gelesen, das Zitat war im Text aber nicht zu finden.";

	/// <summary>Longest value a field read by the agent may have; longer values are skipped, not cut.</summary>
	private const int MaxNameLength = 150;

	private const int MaxLyricsLength = 3500;

	private const int MinQuoteLength = 4;

	private const int MinLyricsQuoteLength = 10;

	private static readonly string[] FieldOrder =
		["composer", "lyricist", "arranger", "musical_key", "voice_label", "lyrics"];

	public async Task RunAsync(
		ArchiveAsset asset, FileRevision revision, string text, Guid? accountId, PrefillProgress progress, CancellationToken token)
	{
		// Only the file the catalogue shows may fill the catalogue: an older
		// revision keeps its text but never writes fields.
		if (asset.CurrentRevisionId != revision.Id || asset.MusicalVersionId is not { } versionId
			|| string.IsNullOrWhiteSpace(text))
			return;
		var version = await db.MusicalVersions
			.Include(v => v.Arrangement).ThenInclude(a => a.Song)
			.FirstOrDefaultAsync(v => v.Id == versionId, token);
		if (version is null)
			return;

		var targets = new Dictionary<string, Target>(StringComparer.Ordinal)
		{
			["composer"] = new(FieldCatalog.EntityTypeSong, version.Arrangement.SongId, version.Arrangement.Song.Composer),
			["lyricist"] = new(FieldCatalog.EntityTypeSong, version.Arrangement.SongId, version.Arrangement.Song.Lyricist),
			["lyrics"] = new(FieldCatalog.EntityTypeSong, version.Arrangement.SongId, version.Arrangement.Song.Lyrics),
			["arranger"] = new(FieldCatalog.EntityTypeArrangement, version.ArrangementId, version.Arrangement.Arranger),
			["musical_key"] = new(FieldCatalog.EntityTypeMusicalVersion, version.Id, version.MusicalKey),
			["voice_label"] = new(FieldCatalog.EntityTypeAsset, asset.Id, asset.VoiceLabel),
		};
		var open = await OpenFieldsAsync(targets, token);
		if (open.Count == 0)
			return;

		var description = SourceDescription(revision);

		// 1. Deterministic: what the analyzer reads, written as regex facts.
		var facts = ScoreTextAnalyzer.Analyze(text).Facts;
		foreach (var (field, value) in RegexFacts(facts))
		{
			if (!open.Contains(field))
				continue;
			var target = targets[field];
			var outcome = await writer.WriteFieldAsync(new AutomatedFieldWriter.FieldWriteRequest(
				target.EntityType, target.EntityId, field, value, ProvenanceSource.Regex, ProvenanceConfidence.Sicher,
				Model: null, PromptVersion: null, RegexReason, description, ProposalOnly: false), accountId, token);
			open.Remove(field);
			Count(outcome, progress);
		}
		if (open.Count == 0)
			return;

		// 2. The agent, for the fields that are still empty and unlocked.
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
			var verified = QuoteIsVerified(read, value, text);
			var certain = string.Equals(read.Certainty?.Trim(), "sicher", StringComparison.OrdinalIgnoreCase);
			var target = targets[read.Field];
			if (await HasOpenProposalAsync(target, read.Field, token))
				continue;
			var apply = certain && verified;
			var reason = apply
				? $"Aus dem Scan gelesen: „{Shorten(read.Quote)}“."
				: verified
					? $"Aus dem Scan gelesen, aber nicht sicher: „{Shorten(read.Quote)}“."
					: UnverifiedReason;
			var outcome = await writer.WriteFieldAsync(new AutomatedFieldWriter.FieldWriteRequest(
				target.EntityType, target.EntityId, read.Field, value, ProvenanceSource.Ai,
				apply ? ProvenanceConfidence.Sicher : ProvenanceConfidence.Unsicher,
				model.Model, ScanPrompts.FieldsVersion, reason, description, ProposalOnly: !apply), accountId, token);
			Count(outcome, progress);
		}
	}

	private static void Count(AutomatedFieldWriter.AutomatedWriteOutcome outcome, PrefillProgress progress)
	{
		switch (outcome.Result)
		{
			case AutomatedFieldWriter.AutomatedWriteResult.Applied:
				progress.Applied++;
				break;
			case AutomatedFieldWriter.AutomatedWriteResult.Proposed:
				progress.Proposed++;
				break;
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
		if (facts.Voice is { } voice)
			yield return ("voice_label", voice);
	}

	/// <summary>The fields that are empty and not locked by a human edit.</summary>
	private async Task<HashSet<string>> OpenFieldsAsync(Dictionary<string, Target> targets, CancellationToken token)
	{
		var ids = targets.Values.Select(t => t.EntityId).Distinct().ToList();
		var locked = (await db.FieldProvenance.AsNoTracking()
				.Where(p => ids.Contains(p.EntityId) && p.Locked)
				.Select(p => new { p.EntityType, p.EntityId, p.Field })
				.ToListAsync(token))
			.Select(p => (p.EntityType, p.EntityId, p.Field))
			.ToHashSet();
		return targets
			.Where(pair => string.IsNullOrWhiteSpace(pair.Value.Current)
				&& !locked.Contains((pair.Value.EntityType, pair.Value.EntityId, pair.Key)))
			.Select(pair => pair.Key)
			.ToHashSet(StringComparer.Ordinal);
	}

	private async Task<bool> HasOpenProposalAsync(Target target, string field, CancellationToken token)
	{
		var open = await db.Proposals.AsNoTracking()
			.Where(p => p.Kind == ProposalKind.FieldSuggestion && p.Status == ProposalStatus.Open
				&& p.TargetEntityId == target.EntityId && p.TargetEntityType == target.EntityType)
			.Select(p => p.Payload)
			.ToListAsync(token);
		return open.Any(payload =>
		{
			try { return JsonDocument.Parse(payload).RootElement.GetProperty("field").GetString() == field; }
			catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
		});
	}

	/// <summary>Trims the value and bounds it; null when it is empty or too long to be a plausible reading.</summary>
	private static string? CleanValue(string field, string? raw)
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
	/// repeated blanks); a name must also stand in its quote, so the model
	/// cannot attach an invented name to a real sentence.
	/// </summary>
	public static bool QuoteIsVerified(ReadField read, string value, string text)
	{
		var quote = Normalize(read.Quote);
		var minimum = read.Field == "lyrics" ? MinLyricsQuoteLength : MinQuoteLength;
		if (quote.Length < minimum || !Normalize(text).Contains(quote, StringComparison.Ordinal))
			return false;
		return read.Field is not ("composer" or "lyricist" or "arranger")
			|| quote.Contains(Normalize(value), StringComparison.Ordinal);
	}

	private static string Normalize(string? value)
		=> string.IsNullOrWhiteSpace(value)
			? string.Empty
			: Regex.Replace(value.Replace("­", ""), @"\s+", " ").Trim().ToLowerInvariant();

	private static string Shorten(string? quote)
	{
		var single = Regex.Replace(quote ?? string.Empty, @"\s+", " ").Trim();
		return single.Length <= 160 ? single : single[..160] + "…";
	}

	private static string SourceDescription(FileRevision revision)
	{
		var name = string.IsNullOrWhiteSpace(revision.OriginalFileName) ? "Datei" : revision.OriginalFileName.Trim();
		return $"Noten {(name.Length > 150 ? name[..150] : name)}, Fassung {revision.RevisionNumber}";
	}

	private sealed record Target(string EntityType, Guid EntityId, string? Current);
}
