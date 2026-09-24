namespace Archive.Backend.Catalogue;

/// <summary>
/// Parsed repertoire filter conditions (ARC-023): pure matching helper in the
/// style of <see cref="CatalogueText"/> so the catalogue endpoint stays
/// readable and the InMemory tests agree with PostgreSQL. Conditions combine
/// per level — song-level text conditions hold on the song, arrangement-level
/// conditions (voice configuration, accompaniment) hold on the same
/// arrangement, and material availability counts only current-revision assets
/// of that arrangement's versions, so two sibling arrangements can never
/// collectively satisfy one filter combination.
/// </summary>
/// <param name="VoiceConfiguration">Folded-at-match arrangement text or null.</param>
/// <param name="Accompaniment">Folded-at-match arrangement text or null.</param>
/// <param name="MusicalKey">Folded-at-match version key or null.</param>
/// <param name="Language">Folded-at-match song text or null.</param>
/// <param name="Occasion">Folded-at-match song text or null.</param>
/// <param name="Tag">Folded-at-match song tag or null.</param>
/// <param name="Materials">Recognized material types, sorted and unique.</param>
public sealed record RepertoireFilter(
	string? VoiceConfiguration,
	string? Accompaniment,
	string? MusicalKey,
	string? Language,
	string? Occasion,
	string? Tag,
	IReadOnlyList<string> Materials)
{
	/// <summary>Recognized material types (ARC-015/016); ARC-032 registers the reserved <c>recording</c> extension point here.</summary>
	public static readonly IReadOnlyList<string> KnownMaterials = ["score", "audio", "midi"];

	/// <summary>True when any arrangement-level parameter is present.</summary>
	public bool HasArrangementConditions =>
		VoiceConfiguration is not null || Accompaniment is not null
		|| MusicalKey is not null || Materials.Count > 0;

	/// <summary>True when any filter condition is present at all.</summary>
	public bool HasConditions =>
		HasArrangementConditions || Language is not null || Occasion is not null || Tag is not null;

	/// <summary>True when version-level data (keys or assets) is needed for the evaluation.</summary>
	public bool HasVersionConditions => MusicalKey is not null || Materials.Count > 0;

	/// <summary>
	/// Song-level conditions (<see cref="Language"/>, <see cref="Occasion"/>,
	/// <see cref="Tag"/>) hold on the song itself. Unknown values stay
	/// explicit: a null field only matches when no condition is set.
	/// </summary>
	public bool MatchesSong(string? language, string? occasion, IReadOnlyList<string> tags)
	{
		if (Language is not null && !FoldContains(language, Language))
			return false;
		if (Occasion is not null && !FoldContains(occasion, Occasion))
			return false;
		if (Tag is not null && !tags.Any(t => FoldContains(t, Tag)))
			return false;
		return true;
	}

	/// <summary>
	/// Arrangement-level conditions hold on this arrangement: with a
	/// <see cref="MusicalKey"/> filter one version carries the key and every
	/// selected material type; without it every selected material type is
	/// available on some version of this same arrangement.
	/// </summary>
	public bool MatchesArrangement(string? voiceConfiguration, string? accompaniment,
		IReadOnlyList<RepertoireVersionRow> versions)
	{
		if (VoiceConfiguration is not null && !FoldContains(voiceConfiguration, VoiceConfiguration))
			return false;
		if (Accompaniment is not null && !FoldContains(accompaniment, Accompaniment))
			return false;
		if (MusicalKey is not null)
			return versions.Any(version => FoldContains(version.MusicalKey, MusicalKey)
				&& Materials.All(version.AssetTypes.Contains));
		if (Materials.Count > 0)
			return Materials.All(material => versions.Any(version => version.AssetTypes.Contains(material)));
		return true;
	}

	/// <summary>Folds both sides and checks a substring match; null never matches.</summary>
	private static bool FoldContains(string? haystack, string needle)
		=> haystack is not null && CatalogueText.Fold(haystack).Contains(CatalogueText.Fold(needle));
}

/// <summary>
/// Database-projected musical version row (ARC-023): the folded-at-match key
/// plus the asset types of the version's current-revision assets; pending
/// assets never reach this row.
/// </summary>
public sealed record RepertoireVersionRow(string? MusicalKey, IReadOnlyList<string> AssetTypes);
