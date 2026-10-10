using Archive.Backend.Assets;
using Archive.Backend.Auth;
using Archive.Backend.Catalogue;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Microsoft.EntityFrameworkCore;

namespace Archive.Backend.Provenance;

/// <summary>Result statuses the shared write service reports to its callers.</summary>
public enum WriteOutcomeStatus
{
	/// <summary>The change is saved.</summary>
	Saved = 0,

	/// <summary>The request does not pass validation; endpoints answer 400.</summary>
	Invalid = 1,

	/// <summary>
	/// The edit is stale: a supplied expected row version did not match, or
	/// the save lost a concurrency race. Endpoints answer 409; reloading helps.
	/// </summary>
	Stale = 2,
}

/// <summary>
/// Outcome of a write through the shared write service (ARC-013-1).
/// Validation, attribution and provenance live here alone, so the endpoint
/// lambdas, the proposal handlers and the automated writers cannot drift.
/// </summary>
public sealed record WriteOutcome(WriteOutcomeStatus Status, string? Title)
{
	public static WriteOutcome Saved() => new(WriteOutcomeStatus.Saved, null);

	public static WriteOutcome Invalid(string title) => new(WriteOutcomeStatus.Invalid, title);

	public static WriteOutcome Stale(string title) => new(WriteOutcomeStatus.Stale, title);

	public bool IsSaved => Status == WriteOutcomeStatus.Saved;

	/// <summary>The HTTP status this outcome maps to in an editor endpoint.</summary>
	public int HttpCode => Status == WriteOutcomeStatus.Invalid ? 400 : 409;
}

/// <summary>
/// The writer of an applied field change: null for a human editor (locks the
/// field), or the automated origin (source, confidence, model, prompt
/// version, confirming editor) of a regex or AI write that applied.
/// </summary>
public sealed record AutomatedWrite(
	ProvenanceSource Source,
	ProvenanceConfidence Confidence,
	string? Model,
	string? PromptVersion,
	Guid? ConfirmedByAccountId = null);

/// <summary>The acting account and the moment of the write, as the caller decided them.</summary>
public sealed record WriteActor(Guid AccountId, DateTimeOffset Now);

/// <summary>Field-only patch shape for asset materials (description and voice label).</summary>
public sealed record AssetFieldPatch(string? Description, string? VoiceLabel);

/// <summary>
/// The shared write service of ARC-013-1: applies song, arrangement, musical
/// version, event and asset-field mutations for endpoint lambdas, proposal
/// handlers and automated writers alike. It owns validation, attribution,
/// row-version bumps and per-field provenance in one save.
/// </summary>
public sealed class CatalogueWriteService(ArchiveDbContext db)
{
	public const string SongConcurrencyMessage = "Der Eintrag wurde zwischenzeitlich geändert.";

	public const string ArrangementConcurrencyMessage = "Die Fassung wurde zwischenzeitlich geändert.";

	public const string RevertUnavailableMessage = "Für dieses Feld ist kein früherer Wert bekannt.";

	public const string UnknownFieldMessage = "Unbekanntes Feld.";

	public const string TitleRequiredMessage = "Der Titel ist erforderlich.";

	public const string TitleTooLongMessage = "Der Titel ist zu lang.";

	public const string ComposerTooLongMessage = "Der Komponist ist zu lang.";

	public const string LyricistTooLongMessage = "Der Textdichter ist zu lang.";

	public const string ArrangerTooLongMessage = "Der Arrangeur ist zu lang.";

	public const string VoiceConfigurationTooLongMessage = "Die Stimmverteilung ist zu lang.";

	public const string CreatorTooLongMessage = "Der Ersteller ist zu lang.";

	public const string MusicalKeyTooLongMessage = "Die Tonart ist zu lang.";

	public const string LabelRequiredMessage = "Das Label ist erforderlich.";

	public const string LabelTooLongMessage = "Das Label ist zu lang.";

	public const string DescriptionTooLongMessage = "Die Beschreibung ist zu lang.";

	/// <summary>Entry maximum per tag and per alternate title (ARC-020/023 contract).</summary>
	public const int TagMaxLength = 60;

	public const int MaxTitleListEntryLength = 200;

	public static bool IsEditor(ArchiveAccessDecision decision)
		=> decision.IsAdministrator || decision.Roles.Contains(ArchiveRoles.Editor);

	/// <summary>
	/// Loads the entity a provenance row or a proposal points at (tracked, so
	/// the caller can write through it). Returns null for unknown ids or an
	/// unknown entity type.
	/// </summary>
	public async Task<object?> LoadEntityAsync(string entityType, Guid entityId, CancellationToken token)
		=> entityType switch
		{
			FieldCatalog.EntityTypeSong => await db.Songs
				.Include(s => s.AlternateTitles)
				.Include(s => s.Tags)
				.FirstOrDefaultAsync(s => s.Id == entityId, token),
			FieldCatalog.EntityTypeArrangement => await db.Arrangements
				.Include(a => a.Song)
				.FirstOrDefaultAsync(a => a.Id == entityId, token),
			FieldCatalog.EntityTypeMusicalVersion => await db.MusicalVersions
				.Include(v => v.Arrangement).ThenInclude(a => a.Song)
				.FirstOrDefaultAsync(v => v.Id == entityId, token),
			FieldCatalog.EntityTypeAsset => await db.Assets
				.FirstOrDefaultAsync(a => a.Id == entityId, token),
			FieldCatalog.EntityTypeEvent => await db.Events
				.FirstOrDefaultAsync(e => e.Id == entityId, token),
			_ => null,
		};

	/// <summary>The optimistic row version of any of the five target entity types.</summary>
	public static uint RowVersionOf(object entity) => entity switch
	{
		Song song => song.RowVersion,
		Arrangement arrangement => arrangement.RowVersion,
		MusicalVersion musicalVersion => musicalVersion.RowVersion,
		ArchiveAsset asset => asset.RowVersion,
		ChoirEvent choirEvent => choirEvent.RowVersion,
		_ => throw new InvalidOperationException(
			$"Unbekannter Eintragstyp in RowVersionOf ({entity.GetType().Name})."),
	};

	/// <summary>
	/// The current value of one catalogued field in the joined-string form
	/// provenance and proposals store; null is an unset field.
	/// </summary>
	public static string? CurrentFieldValue(object entity, string field)
	{
		switch (entity, field)
		{
			case (Song song, "title"): return song.Title;
			case (Song song, "composer"): return song.Composer;
			case (Song song, "lyricist"): return song.Lyricist;
			case (Song song, "lyrics"): return song.Lyrics;
			case (Song song, "language"): return song.Language;
			case (Song song, "occasion"): return song.Occasion;
			case (Song song, "alternate_titles"):
				return FieldCatalog.JoinList(song.AlternateTitles
					.OrderBy(t => t.Position).ThenBy(t => t.Id).Select(t => t.Value));
			case (Song song, "tags"):
				return FieldCatalog.JoinList(song.Tags
					.OrderBy(t => t.Position).ThenBy(t => t.Id).Select(t => t.Value));
			case (Arrangement arrangement, "label"): return arrangement.Label;
			case (Arrangement arrangement, "arranger"): return arrangement.Arranger;
			case (Arrangement arrangement, "voice_configuration"): return arrangement.VoiceConfiguration;
			case (Arrangement arrangement, "accompaniment"): return arrangement.Accompaniment;
			case (MusicalVersion version, "label"): return version.Label;
			case (MusicalVersion version, "creator"): return version.Creator;
			case (MusicalVersion version, "musical_key"): return version.MusicalKey;
			case (ArchiveAsset asset, "description"): return asset.Description;
			case (ArchiveAsset asset, "voice_label"): return asset.VoiceLabel;
			case (ChoirEvent choirEvent, "notes"): return choirEvent.Notes;
			case (ChoirEvent choirEvent, "source_note"): return choirEvent.SourceNote;
			default: throw new InvalidOperationException($"Unbekanntes Feld {field} in CurrentFieldValue.");
		}
	}

	/// <summary>
	/// Writes exactly one catalogued field with the given value through the
	/// ordinary patch path of its entity; a null value clears the field.
	/// </summary>
	public async Task<WriteOutcome> WriteFieldAsync(
		object entity, string field, string? value, WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		switch (entity, field)
		{
			case (Song song, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeSong, f):
				return await PatchSongAsync(song, SingleFieldSongRequest(f, value), null, actor, origin, token);
			case (Arrangement arrangement, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeArrangement, f):
				return await PatchArrangementAsync(arrangement, SingleFieldPatch(f, value), null, actor, origin, token);
			case (MusicalVersion version, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeMusicalVersion, f):
				return await PatchMusicalVersionAsync(version, SingleVersionPatch(f, value), null, actor, origin, token);
			case (ArchiveAsset, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeAsset, f):
				return await PatchAssetAsync((ArchiveAsset)entity, new AssetFieldPatch(
					Description: f == "description" ? value : null,
					VoiceLabel: f == "voice_label" ? value : null), actor, origin, token);
			case (ChoirEvent choirEvent, var f) when FieldCatalog.IsKnownField(FieldCatalog.EntityTypeEvent, f):
				return await PatchEventAsync(choirEvent, new PatchEventRequest(
					Title: null, Kind: null, Venue: null, Date: null, StartTime: null,
					Notes: f == "notes" ? value : null,
					SourceNote: f == "source_note" ? value : null,
					ExpectedRowVersion: null), actor, origin, token);
			default:
				return WriteOutcome.Invalid(UnknownFieldMessage);
		}
	}

	/// <summary>
	/// Restores the previous value of one field from its provenance row and
	/// locks the field the same way a human edit would.
	/// </summary>
	public sealed record FieldRevertOutcome(WriteOutcome Outcome)
	{
		public bool IsSaved => Outcome.IsSaved;
	}

	public async Task<FieldRevertOutcome> RevertFieldAsync(
		string entityType, Guid entityId, string field, WriteActor actor, CancellationToken token)
	{
		if (!FieldCatalog.IsKnownField(entityType, field))
			return new(WriteOutcome.Invalid(UnknownFieldMessage));
		var provenance = await db.FieldProvenance
			.FirstOrDefaultAsync(p => p.EntityType == entityType && p.EntityId == entityId && p.Field == field, token);
		if (provenance is null)
			return new(WriteOutcome.Stale(RevertUnavailableMessage));
		var entity = await LoadEntityAsync(entityType, entityId, token);
		if (entity is null)
			return new(WriteOutcome.Stale(RevertUnavailableMessage));
		return new(await WriteFieldAsync(entity, field, provenance.PreviousValue, actor, null, token));
	}

	// ---- Songs -------------------------------------------------------------

	/// <summary>Creates a song with its initial arrangement and version; runs from endpoints and from proposal handlers.</summary>
	public async Task<(WriteOutcome Outcome, Song? Song)> CreateSongAsync(
		CreateSongRequest request, WriteActor actor, CancellationToken token)
	{
		var title = (request.Title ?? string.Empty).Trim();
		if (title.Length == 0)
			return (WriteOutcome.Invalid(TitleRequiredMessage), null);
		if (title.Length > 200)
			return (WriteOutcome.Invalid(TitleTooLongMessage), null);
		var composer = CleanCreate(request.Composer, 200, ComposerTooLongMessage, out var composerError);
		if (composerError is not null) return (composerError, null);
		var lyricist = CleanCreate(request.Lyricist, 200, LyricistTooLongMessage, out var lyricistError);
		if (lyricistError is not null) return (lyricistError, null);
		var arrangementLabel = CleanCreate(request.ArrangementLabel, 200, LabelTooLongMessage, out var arrangementError);
		if (arrangementError is not null) return (arrangementError, null);
		var versionLabel = CleanCreate(request.VersionLabel, 200, LabelTooLongMessage, out var versionError);
		if (versionError is not null) return (versionError, null);
		var language = CleanCreate(request.Language, 200, CatalogueEndpoints.LanguageTooLongMessage, out var languageError);
		if (languageError is not null) return (languageError, null);
		var occasion = CleanCreate(request.Occasion, 200, CatalogueEndpoints.OccasionTooLongMessage, out var occasionError);
		if (occasionError is not null) return (occasionError, null);
		if (request.Tags is not null
			&& !ValidateEntryList(request.Tags, CatalogueEndpoints.TagTooManyMessage,
				CatalogueEndpoints.TagEmptyMessage, CatalogueEndpoints.TagTooLongMessage, TagMaxLength, out var tagError))
			return (tagError, null);

		var song = new Song
		{
			Title = title,
			Composer = composer,
			Lyricist = lyricist,
			Language = language,
			Occasion = occasion,
			CreatedAt = actor.Now,
			CreatedByAccountId = actor.AccountId,
			UpdatedAt = actor.Now,
			UpdatedByAccountId = actor.AccountId,
		};
		if (request.Tags is not null)
		{
			for (var position = 0; position < request.Tags.Count; position++)
			{
				song.Tags.Add(new SongTag
				{
					Song = song,
					Value = request.Tags[position]!.Trim(),
					Position = position,
					CreatedAt = actor.Now,
					CreatedByAccountId = actor.AccountId,
				});
			}
		}
		song.Arrangements.Add(new Arrangement
		{
			Song = song,
			Label = arrangementLabel ?? CatalogueEndpoints.DefaultLabel,
			CreatedAt = actor.Now,
			CreatedByAccountId = actor.AccountId,
			MusicalVersions =
			[
				new MusicalVersion
				{
					Label = versionLabel ?? CatalogueEndpoints.DefaultLabel,
					CreatedAt = actor.Now,
					CreatedByAccountId = actor.AccountId,
				},
			],
		});
		db.Songs.Add(song);
		try
		{
			await db.SaveChangesAsync(token);
		}
		catch (DbUpdateConcurrencyException)
		{
			return (WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage), null);
		}
		return (WriteOutcome.Saved(), song);
	}

	/// <summary>
	/// Applies one song patch; validation, attribution, provenance and the
	/// row-version bump all live here. A supplied expected row version makes
	/// a stale edit a refused write without touching the entity.
	/// </summary>
	public async Task<WriteOutcome> PatchSongAsync(
		Song song, PatchSongRequest request, uint? expectedRowVersion,
		WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		var titleBefore = song.Title;
		string? title = null;
		if (request.Title is not null)
		{
			title = request.Title.Trim();
			if (title.Length == 0)
				return WriteOutcome.Invalid(TitleRequiredMessage);
			if (title.Length > 200)
				return WriteOutcome.Invalid(TitleTooLongMessage);
		}
		var composer = CleanPatch(request.Composer, 200, ComposerTooLongMessage, out var composerError);
		if (composerError is not null) return composerError;
		var lyricist = CleanPatch(request.Lyricist, 200, LyricistTooLongMessage, out var lyricistError);
		if (lyricistError is not null) return lyricistError;
		var language = CleanPatch(request.Language, 200, CatalogueEndpoints.LanguageTooLongMessage, out var languageError);
		if (languageError is not null) return languageError;
		var occasion = CleanPatch(request.Occasion, 200, CatalogueEndpoints.OccasionTooLongMessage, out var occasionError);
		if (occasionError is not null) return occasionError;
		string? lyrics = null;
		if (request.Lyrics is not null)
		{
			var trimmedLyrics = request.Lyrics.Trim();
			if (trimmedLyrics.Length > 5000)
				return WriteOutcome.Invalid(CatalogueEndpoints.LyricsTooLongMessage);
			lyrics = trimmedLyrics.Length == 0 ? null : trimmedLyrics;
		}
		List<string>? alternateTitles = null;
		if (request.AlternateTitles is not null)
		{
			if (!ValidateEntryList(request.AlternateTitles,
					CatalogueEndpoints.AlternateTitleTooManyMessage, CatalogueEndpoints.AlternateTitleEmptyMessage,
					CatalogueEndpoints.AlternateTitleTooLongMessage, MaxTitleListEntryLength, out var alternateError))
				return alternateError;
			alternateTitles = request.AlternateTitles.Select(entry => entry!.Trim()).ToList();
		}
		List<string>? tags = null;
		if (request.Tags is not null)
		{
			if (!ValidateEntryList(request.Tags,
					CatalogueEndpoints.TagTooManyMessage, CatalogueEndpoints.TagEmptyMessage,
					CatalogueEndpoints.TagTooLongMessage, TagMaxLength, out var tagValidationError))
				return tagValidationError;
			tags = request.Tags.Select(entry => entry!.Trim()).ToList();
		}
		if (expectedRowVersion is not null && expectedRowVersion != song.RowVersion)
			return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage);

		var composerBefore = song.Composer;
		var lyricistBefore = song.Lyricist;
		var lyricsBefore = song.Lyrics;
		var languageBefore = song.Language;
		var occasionBefore = song.Occasion;
		var alternateBefore = request.AlternateTitles is null
			? null
			: FieldCatalog.JoinList(song.AlternateTitles
				.OrderBy(t => t.Position).ThenBy(t => t.Id).Select(t => t.Value));
		var tagsBefore = request.Tags is null
			? null
			: FieldCatalog.JoinList(song.Tags
				.OrderBy(t => t.Position).ThenBy(t => t.Id).Select(t => t.Value));

		var titleAfter = After(title is not null, title, titleBefore);
		if (title is not null)
			song.Title = title;
		if (request.Composer is not null)
			song.Composer = composer;
		if (request.Lyricist is not null)
			song.Lyricist = lyricist;
		if (request.Lyrics is not null)
			song.Lyrics = lyrics;
		if (request.Language is not null)
			song.Language = language;
		if (request.Occasion is not null)
			song.Occasion = occasion;
		if (alternateTitles is not null)
		{
			// Full replacement: fresh rows in list order (Guid v7 ids share
			// the millisecond of this save). Lists keep their entered-by
			// attribution; automated runs preserve the song's creator.
			db.SongTitles.RemoveRange(song.AlternateTitles);
			var listActor = origin is null ? actor.AccountId : song.CreatedByAccountId;
			foreach (var (value, position) in alternateTitles.Select((v, p) => (v, p)))
			{
				db.SongTitles.Add(new SongTitle
				{
					SongId = song.Id,
					Value = value,
					Position = position,
					CreatedAt = actor.Now,
					CreatedByAccountId = listActor,
				});
			}
		}
		if (tags is not null)
		{
			db.SongTags.RemoveRange(song.Tags);
			var listActor = origin is null ? actor.AccountId : song.CreatedByAccountId;
			foreach (var (value, position) in tags.Select((v, p) => (v, p)))
			{
				db.SongTags.Add(new SongTag
				{
					SongId = song.Id,
					Value = value,
					Position = position,
					CreatedAt = actor.Now,
					CreatedByAccountId = listActor,
				});
			}
		}
		var alternateAfter = alternateTitles is null
			? null
			: FieldCatalog.JoinList(alternateTitles);
		var tagsAfter = tags is null ? null : FieldCatalog.JoinList(tags);

		await RecordProvenanceAsync(FieldCatalog.EntityTypeSong, song.Id,
		[
			("title", titleBefore, titleAfter),
			("composer", composerBefore, After(request.Composer is not null, composer, composerBefore)),
			("lyricist", lyricistBefore, After(request.Lyricist is not null, lyricist, lyricistBefore)),
			("lyrics", lyricsBefore, After(request.Lyrics is not null, lyrics, lyricsBefore)),
			("language", languageBefore, After(request.Language is not null, language, languageBefore)),
			("occasion", occasionBefore, After(request.Occasion is not null, occasion, occasionBefore)),
			("alternate_titles", alternateBefore, alternateAfter),
			("tags", tagsBefore, tagsAfter),
		], actor, origin, token);

		return await SaveSongAttributionAsync(song, actor, origin, SongConcurrencyMessage, token);
	}

	public async Task<WriteOutcome> PublishSongAsync(Song song, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (song.PublishedAt is not null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != song.RowVersion)
			return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage);
		song.PublishedAt = actor.Now;
		song.PublishedByAccountId = actor.AccountId;
		song.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	public async Task<WriteOutcome> UnpublishSongAsync(Song song, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (song.PublishedAt is null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != song.RowVersion)
			return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage);
		song.PublishedAt = null;
		song.PublishedByAccountId = null;
		song.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	/// <summary>Creates one further arrangement under an existing song.</summary>
	public async Task<WriteOutcome> CreateArrangementAsync(
		Song song, CreateArrangementRequest request, WriteActor actor, CancellationToken token)
	{
		var label = (request.Label ?? string.Empty).Trim();
		if (label.Length == 0)
			return WriteOutcome.Invalid(LabelRequiredMessage);
		if (label.Length > 200)
			return WriteOutcome.Invalid(LabelTooLongMessage);
		var arranger = CleanCreate(request.Arranger, 200, ArrangerTooLongMessage, out var arrangerError);
		if (arrangerError is not null) return arrangerError;
		var voiceConfiguration = CleanCreate(request.VoiceConfiguration, 200, VoiceConfigurationTooLongMessage, out var voiceError);
		if (voiceError is not null) return voiceError;
		var accompaniment = CleanCreate(request.Accompaniment, 200, CatalogueEndpoints.AccompanimentTooLongMessage, out var accompanimentError);
		if (accompanimentError is not null) return accompanimentError;
		db.Arrangements.Add(new Arrangement
		{
			SongId = song.Id,
			Label = label,
			Arranger = arranger,
			VoiceConfiguration = voiceConfiguration,
			Accompaniment = accompaniment,
			CreatedAt = actor.Now,
			CreatedByAccountId = actor.AccountId,
		});
		return await SaveSongAttributionAsync(song, actor, null, CatalogueEndpoints.ConcurrencyMessage, token);
	}

	/// <summary>Creates one further musical version under an existing arrangement.</summary>
	public async Task<WriteOutcome> CreateMusicalVersionAsync(
		Arrangement arrangement, CreateMusicalVersionRequest request, WriteActor actor, CancellationToken token)
	{
		var label = (request.Label ?? string.Empty).Trim();
		if (label.Length == 0)
			return WriteOutcome.Invalid(LabelRequiredMessage);
		if (label.Length > 200)
			return WriteOutcome.Invalid(LabelTooLongMessage);
		var creator = CleanCreate(request.Creator, 200, CreatorTooLongMessage, out var creatorError);
		if (creatorError is not null) return creatorError;
		var musicalKey = CleanCreate(request.MusicalKey, 200, MusicalKeyTooLongMessage, out var keyError);
		if (keyError is not null) return keyError;
		db.MusicalVersions.Add(new MusicalVersion
		{
			ArrangementId = arrangement.Id,
			Label = label,
			Creator = creator,
			MusicalKey = musicalKey,
			CreatedAt = actor.Now,
			CreatedByAccountId = actor.AccountId,
		});
		return await SaveSongAttributionAsync(arrangement.Song, actor, null, CatalogueEndpoints.ConcurrencyMessage, token);
	}

	/// <summary>Applies one arrangement patch with its own row version and the song's attribution.</summary>
	public async Task<WriteOutcome> PatchArrangementAsync(
		Arrangement arrangement, PatchArrangementRequest request, uint? expectedRowVersion,
		WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		var labelBefore = arrangement.Label;
		string? label = null;
		if (request.Label is not null)
		{
			label = request.Label.Trim();
			if (label.Length == 0)
				return WriteOutcome.Invalid(LabelRequiredMessage);
			if (label.Length > 200)
				return WriteOutcome.Invalid(LabelTooLongMessage);
		}
		var arranger = CleanPatch(request.Arranger, 200, ArrangerTooLongMessage, out var arrangerError);
		if (arrangerError is not null) return arrangerError;
		var voiceConfiguration = CleanPatch(request.VoiceConfiguration, 200, VoiceConfigurationTooLongMessage, out var voiceError);
		if (voiceError is not null) return voiceError;
		var accompaniment = CleanPatch(request.Accompaniment, 200, CatalogueEndpoints.AccompanimentTooLongMessage, out var accompanimentError);
		if (accompanimentError is not null) return accompanimentError;
		if (expectedRowVersion is not null && expectedRowVersion != arrangement.RowVersion)
			return WriteOutcome.Stale(ArrangementConcurrencyMessage);

		var arrangerBefore = arrangement.Arranger;
		var voiceConfigurationBefore = arrangement.VoiceConfiguration;
		var accompanimentBefore = arrangement.Accompaniment;
		if (label is not null)
			arrangement.Label = label;
		if (request.Arranger is not null)
			arrangement.Arranger = arranger;
		if (request.VoiceConfiguration is not null)
			arrangement.VoiceConfiguration = voiceConfiguration;
		if (request.Accompaniment is not null)
			arrangement.Accompaniment = accompaniment;

		await RecordProvenanceAsync(FieldCatalog.EntityTypeArrangement, arrangement.Id,
		[
			("label", labelBefore, After(label is not null, label, labelBefore)),
			("arranger", arrangerBefore, After(request.Arranger is not null, arranger, arrangerBefore)),
			("voice_configuration", voiceConfigurationBefore, After(request.VoiceConfiguration is not null, voiceConfiguration, voiceConfigurationBefore)),
			("accompaniment", accompanimentBefore, After(request.Accompaniment is not null, accompaniment, accompanimentBefore)),
		], actor, origin, token);

		arrangement.RowVersion++;
		return await SaveSongAttributionAsync(arrangement.Song, actor, origin, SongConcurrencyMessage, token);
	}

	/// <summary>Applies one musical-version patch with its own row version and the song's attribution.</summary>
	public async Task<WriteOutcome> PatchMusicalVersionAsync(
		MusicalVersion version, PatchMusicalVersionRequest request, uint? expectedRowVersion,
		WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		var labelBefore = version.Label;
		string? label = null;
		if (request.Label is not null)
		{
			label = request.Label.Trim();
			if (label.Length == 0)
				return WriteOutcome.Invalid(LabelRequiredMessage);
			if (label.Length > 200)
				return WriteOutcome.Invalid(LabelTooLongMessage);
		}
		var creator = CleanPatch(request.Creator, 200, CreatorTooLongMessage, out var creatorError);
		if (creatorError is not null) return creatorError;
		var musicalKey = CleanPatch(request.MusicalKey, 200, MusicalKeyTooLongMessage, out var keyError);
		if (keyError is not null) return keyError;
		if (expectedRowVersion is not null && expectedRowVersion != version.RowVersion)
			return WriteOutcome.Stale(ArrangementConcurrencyMessage);

		var creatorBefore = version.Creator;
		var musicalKeyBefore = version.MusicalKey;
		if (label is not null)
			version.Label = label;
		if (request.Creator is not null)
			version.Creator = creator;
		if (request.MusicalKey is not null)
			version.MusicalKey = musicalKey;

		await RecordProvenanceAsync(FieldCatalog.EntityTypeMusicalVersion, version.Id,
		[
			("label", labelBefore, After(label is not null, label, labelBefore)),
			("creator", creatorBefore, After(request.Creator is not null, creator, creatorBefore)),
			("musical_key", musicalKeyBefore, After(request.MusicalKey is not null, musicalKey, musicalKeyBefore)),
		], actor, origin, token);

		version.RowVersion++;
		return await SaveSongAttributionAsync(version.Arrangement.Song, actor, origin, SongConcurrencyMessage, token);
	}

	// ---- Events ------------------------------------------------------------

	/// <summary>Applies one event patch; validation, provenance attribution and the row-version bump live here.</summary>
	public async Task<WriteOutcome> PatchEventAsync(
		ChoirEvent choirEvent, PatchEventRequest request, WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		var titleBefore = choirEvent.Title;
		string? title = null;
		if (request.Title is not null)
		{
			title = request.Title.Trim();
			if (title.Length == 0)
				return WriteOutcome.Invalid(EventEndpoints.TitleRequiredMessage);
			if (title.Length > EventEndpoints.TitleMaxLength)
				return WriteOutcome.Invalid(EventEndpoints.TitleTooLongMessage);
		}
		string? kind = null;
		if (request.Kind is not null)
		{
			kind = request.Kind.Trim();
			if (!EventKinds.Known.Contains(kind))
				return WriteOutcome.Invalid(EventEndpoints.KindUnknownMessage);
		}
		var venue = CleanPatch(request.Venue, EventEndpoints.VenueMaxLength, EventEndpoints.VenueTooLongMessage, out var venueError);
		if (venueError is not null) return venueError;
		var notes = CleanPatch(request.Notes, EventEndpoints.NotesMaxLength, EventEndpoints.NotesTooLongMessage, out var notesError);
		if (notesError is not null) return notesError;
		var sourceNote = CleanPatch(request.SourceNote, EventEndpoints.SourceNoteMaxLength, EventEndpoints.SourceNoteTooLongMessage, out var sourceNoteError);
		if (sourceNoteError is not null) return sourceNoteError;
		string? startTime = null;
		if (request.StartTime is not null)
		{
			var trimmed = request.StartTime.Trim();
			// Present but empty clears the optional time; otherwise it must
			// parse exactly as "HH:mm".
			if (trimmed.Length > 0 && !TimeOnly.TryParseExact(trimmed, "HH:mm",
					System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.None, out _))
				return WriteOutcome.Invalid(EventEndpoints.StartTimeInvalidMessage);
			startTime = trimmed.Length == 0 ? null : trimmed;
		}
		if (request.Date is not null
			&& !ValidateEventDate(request.Date.Year, request.Date.Month, request.Date.Day, out var dateTitle))
			return WriteOutcome.Invalid(dateTitle);
		if (request.ExpectedRowVersion is not null && request.ExpectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);

		var notesBefore = choirEvent.Notes;
		var sourceNoteBefore = choirEvent.SourceNote;
		if (title is not null)
			choirEvent.Title = title;
		if (kind is not null)
			choirEvent.Kind = kind;
		if (request.Venue is not null)
			choirEvent.Venue = venue;
		if (request.StartTime is not null)
			choirEvent.StartTime = startTime;
		if (request.Notes is not null)
			choirEvent.Notes = notes;
		if (request.SourceNote is not null)
			choirEvent.SourceNote = sourceNote;
		if (request.Date is not null)
		{
			// The nested date object is an explicit full replacement; a null
			// year means unknown date.
			choirEvent.DateYear = request.Date.Year;
			choirEvent.DateMonth = request.Date.Month;
			choirEvent.DateDay = request.Date.Day;
			choirEvent.DateApproximate = request.Date.Approximate;
		}

		await RecordProvenanceAsync(FieldCatalog.EntityTypeEvent, choirEvent.Id,
		[
			("notes", notesBefore, After(request.Notes is not null, notes, notesBefore)),
			("source_note", sourceNoteBefore, After(request.SourceNote is not null, sourceNote, sourceNoteBefore)),
		], actor, origin, token);

		choirEvent.UpdatedAt = actor.Now;
		choirEvent.UpdatedByAccountId = origin is null ? actor.AccountId : choirEvent.CreatedByAccountId;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	/// <summary>Publishes one event; identity and visibility stay human-confirmed or proposal-applied.</summary>
	public async Task<WriteOutcome> PublishEventAsync(ChoirEvent choirEvent, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (choirEvent.PublishedAt is not null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);
		choirEvent.PublishedAt = actor.Now;
		choirEvent.PublishedByAccountId = actor.AccountId;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	public async Task<WriteOutcome> UnpublishEventAsync(ChoirEvent choirEvent, WriteActor actor, uint? expectedRowVersion, CancellationToken token)
	{
		if (choirEvent.PublishedAt is null)
			return WriteOutcome.Saved();
		if (expectedRowVersion is not null && expectedRowVersion != choirEvent.RowVersion)
			return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage);
		choirEvent.PublishedAt = null;
		choirEvent.PublishedByAccountId = null;
		choirEvent.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(EventEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	// ---- Asset fields ------------------------------------------------------

	/// <summary>
	/// Applies an asset-field patch (description, voice label) with
	/// provenance; the asset type and file stay untouched here and the
	/// ownership checks remain in the endpoints.
	/// </summary>
	public async Task<WriteOutcome> PatchAssetAsync(
		ArchiveAsset asset, AssetFieldPatch patch, WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		string? description = null;
		if (patch.Description is not null)
		{
			description = patch.Description.Trim();
			if (description.Length == 0)
				description = null;
			else if (description.Length > 500)
				return WriteOutcome.Invalid(DescriptionTooLongMessage);
		}
		string? voiceLabel = null;
		if (patch.VoiceLabel is not null)
		{
			voiceLabel = patch.VoiceLabel.Trim();
			if (voiceLabel.Length == 0)
				voiceLabel = null;
			else if (voiceLabel.Length > 200)
				return WriteOutcome.Invalid(ArrangerTooLongMessage);
		}

		var descriptionBefore = asset.Description;
		var voiceBefore = asset.VoiceLabel;
		if (patch.Description is not null)
			asset.Description = description;
		if (patch.VoiceLabel is not null)
			asset.VoiceLabel = voiceLabel;

		await RecordProvenanceAsync(FieldCatalog.EntityTypeAsset, asset.Id,
		[
			("description", descriptionBefore, After(patch.Description is not null, description, descriptionBefore)),
			("voice_label", voiceBefore, After(patch.VoiceLabel is not null, voiceLabel, voiceBefore)),
		], actor, origin, token);

		asset.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(CatalogueEndpoints.ConcurrencyMessage); }
		return WriteOutcome.Saved();
	}

	// ---- Internal helpers --------------------------------------------------

	/// <summary>The value a field carries after this request: applied when present, its previous value otherwise.</summary>
	private static string? After(bool present, string? applied, string? before)
		=> present ? applied : before;

	/// <summary>Trims an optional write value for a patch; empty means clear (null), too long is an invalid write.</summary>
	private static string? CleanPatch(string? raw, int maxLength, string tooLongTitle, out WriteOutcome? error)
	{
		error = null;
		var trimmed = raw?.Trim();
		if (trimmed is not null && trimmed.Length > maxLength)
		{
			error = new WriteOutcome(WriteOutcomeStatus.Invalid, tooLongTitle);
			return null;
		}
		return string.IsNullOrEmpty(trimmed) ? null : trimmed;
	}

	/// <summary>Trims an optional create value; null or empty stays after optional, too long is an invalid write.</summary>
	private static string? CleanCreate(string? raw, int maxLength, string tooLongTitle, out WriteOutcome? error)
		=> CleanPatch(raw, maxLength, tooLongTitle, out error);

	private static bool ValidateEntryList(
		List<string?>? entries, string tooManyTitle, string emptyTitle, string tooLongTitle, int maxLength,
		out WriteOutcome error)
	{
		if (entries is null)
		{
			error = WriteOutcome.Saved();
			return true;
		}
		if (entries.Count > 10)
		{
			error = WriteOutcome.Invalid(tooManyTitle);
			return false;
		}
		foreach (var entry in entries)
		{
			var trimmed = entry?.Trim();
			if (string.IsNullOrEmpty(trimmed))
			{
				error = WriteOutcome.Invalid(emptyTitle);
				return false;
			}
			if (trimmed.Length > maxLength)
			{
				error = WriteOutcome.Invalid(tooLongTitle);
				return false;
			}
		}
		error = WriteOutcome.Saved();
		return true;
	}

	/// <summary>Validates one explicit ARC-024 date block: only (nothing), (year), (year, month) and (year, month, day) are valid.</summary>
	private static bool ValidateEventDate(int? year, int? month, int? day, out string title)
	{
		if (month is not null && year is null)
		{
			title = EventEndpoints.IncompleteDateMessage;
			return false;
		}
		if (day is not null && month is null)
		{
			title = EventEndpoints.IncompleteDateMessage;
			return false;
		}
		if (year is not null && year is < 1800 or > 2100)
		{
			title = EventEndpoints.YearOutOfRangeMessage;
			return false;
		}
		if (month is not null && month is < 1 or > 12)
		{
			title = EventEndpoints.InvalidDateMessage;
			return false;
		}
		if (day is not null && (day is < 1 || day.Value > DateTime.DaysInMonth(year!.Value, month!.Value)))
		{
			title = EventEndpoints.InvalidDateMessage;
			return false;
		}
		title = string.Empty;
		return true;
	}

	/// <summary>Builds a song patch request that only touches one field.</summary>
	private static PatchSongRequest SingleFieldSongRequest(string field, string? value) => new(
		Title: field == "title" ? value : null,
		Composer: field == "composer" ? value : null,
		Lyricist: field == "lyricist" ? value : null,
		Lyrics: field == "lyrics" ? value : null,
		Language: field == "language" ? value : null,
		Occasion: field == "occasion" ? value : null,
		AlternateTitles: field == "alternate_titles"
			? FieldCatalog.SplitList(value).Cast<string?>().ToList()
			: null,
		Tags: field == "tags"
			? FieldCatalog.SplitList(value).Cast<string?>().ToList()
			: null,
		RowVersion: null);

	/// <summary>Builds an arrangement or musical-version patch request that only touches one field.</summary>
	private static PatchArrangementRequest SingleFieldPatch(string field, string? value) => new(
		Label: field == "label" ? value : null,
		Arranger: field == "arranger" ? value : null,
		VoiceConfiguration: field == "voice_configuration" ? value : null,
		Accompaniment: field == "accompaniment" ? value : null,
		RowVersion: null);

	private static PatchMusicalVersionRequest SingleVersionPatch(string field, string? value) => new(
		Label: field == "label" ? value : null,
		Creator: field == "creator" ? value : null,
		MusicalKey: field == "musical_key" ? value : null,
		RowVersion: null);

	/// <summary>
	/// Upserts the latest provenance row per changed field. Human writes and
	/// reverts lock the field; automated writes keep it open for later runs
	/// and name the run (source, confidence, model, prompt version).
	/// </summary>
	private async Task RecordProvenanceAsync(
		string entityType, Guid entityId, List<(string Field, string? Before, string? After)> changes,
		WriteActor actor, AutomatedWrite? origin, CancellationToken token)
	{
		foreach (var (field, before, after) in changes)
		{
			if (!FieldCatalog.IsKnownField(entityType, field))
				continue;
			// (null, null) marks a field absent from this request.
			if (before is null && after is null)
				continue;
			if (string.Equals(before, after, StringComparison.Ordinal))
				continue;
			var row = await db.FieldProvenance
				.FirstOrDefaultAsync(p => p.EntityType == entityType
					&& p.EntityId == entityId && p.Field == field, token);
			if (row is null)
			{
				row = new FieldProvenance { EntityType = entityType, EntityId = entityId, Field = field };
				db.FieldProvenance.Add(row);
			}
			row.Source = origin is null ? ProvenanceSource.Human : origin.Source;
			row.Confidence = origin is null ? ProvenanceConfidence.Sicher : origin.Confidence;
			row.Model = origin?.Model;
			row.PromptVersion = origin?.PromptVersion;
			row.PreviousValue = before;
			row.ChangedAt = actor.Now;
			row.ActorAccountId = origin is null ? actor.AccountId : origin.ConfirmedByAccountId;
			row.Locked = origin is null;
		}
	}

	/// <summary>Saves with the song-level attribution (UpdatedAt, UpdatedBy, RowVersion bump) and the stale mapping.</summary>
	private async Task<WriteOutcome> SaveSongAttributionAsync(
		Song song, WriteActor actor, AutomatedWrite? origin, string staleMessage, CancellationToken token)
	{
		song.UpdatedAt = actor.Now;
		song.UpdatedByAccountId = origin is null ? actor.AccountId : song.UpdatedByAccountId;
		song.RowVersion++;
		try { await db.SaveChangesAsync(token); }
		catch (DbUpdateConcurrencyException) { return WriteOutcome.Stale(staleMessage); }
		return WriteOutcome.Saved();
	}
}
