using Archive.Backend.Catalogue;
using Archive.Backend.Lieder;

namespace Archive.Backend.Tests;

/// <summary>
/// #192: one search module owns folding, matching, ranking and paging, pure
/// over rows and without a database. The catalog decides what is searchable,
/// so a field without a search label is not searchable by accident.
/// </summary>
public sealed class LiedSearchModuleTests
{
	private static readonly IReadOnlyDictionary<Guid, string> KeineNoten = new Dictionary<Guid, string>();

	private static Lied Lied(string title, Action<Lied>? felder = null)
	{
		var lied = new Lied { Title = title };
		felder?.Invoke(lied);
		return lied;
	}

	private static Lied MitFeld(Func<LiedField, bool> praedikat, string value) => Lied("", lied =>
	{
		var entry = FieldCatalog.LiedFields.First(praedikat);
		entry.Set(lied, value);
		lied.Title = "Titel";
	});

	[Fact]
	public void TokensOfAreFoldedAndDropEverythingEmpty()
	{
		Assert.Equal(["uber", "den", "wolken"], LiedSearch.TokensOf("ÜBER den  Wolken"));
		Assert.Empty(LiedSearch.TokensOf("  \t "));
	}

	[Fact]
	public void AFieldWithoutASearchLabelIsNotSearchedByAccident()
	{
		// Any field the catalog does not mark searchable stays invisible,
		// whatever the browser sends and wherever the chance comes from.
		var unsichtbar = FieldCatalog.LiedFields.First(f => f.SearchLabel is null);
		var lied = MitFeld(f => f.SearchLabel is null, "einzigartigtrommelwande");
		Assert.Empty(LiedSearch.Match(LiedSearch.TokensOf("trommelwande"), [lied], KeineNoten, "trommelwande"));
	}

	[Fact]
	public void FieldsOfExposeOnlyTitleTheSearchableFieldsAndTheNoten()
	{
		// #192: even where every field carries a value, only the title, the
		// fields the catalog marks searchable and the Noten text contribute;
		// a field without a search label never joins on its own.
		var lied = Lied("Titel");
		foreach (var field in FieldCatalog.LiedFields)
			field.Set(lied, "warmwort");
		var fields = LiedSearch.FieldsOf(lied, "notenwort");
		Assert.Equal(
			[LiedSearch.TitleLabel, .. FieldCatalog.LiedSearchFields.Select(f => f.SearchLabel!), LiedSearch.NotenLabel],
			fields.Select(f => f.Label).ToArray());
	}

	[Fact]
	public void EveryTokenMustStandInAtLeastOneField()
	{
		var lied = Lied("Hosianna", lied => FieldCatalog.LiedField("composer").Set(lied, "Jakob Hosianna Komponist"));
		Assert.NotEmpty(LiedSearch.Match(LiedSearch.TokensOf("hosianna komponist"), [lied], KeineNoten, "x y"));
		Assert.Empty(LiedSearch.Match(LiedSearch.TokensOf("hosianna fehlt"), [lied], KeineNoten, "x y"));
	}

	[Fact]
	public void RankingPrefersTheExactTitleThenTheTitleThenTheRest()
	{
		var exact = Lied("Uber Wolken", lied => FieldCatalog.LiedField("composer").Set(lied, "Wolken Jakob"));
		var inTitle = Lied("Uber Wolken Morgen");
		var sonst = Lied("Anderes", lied => FieldCatalog.LiedField("lyrics").Set(lied, "uber wolken lieber Jakob"));
		var query = LiedSearch.TokensOf("uber wolken");
		var matches = LiedSearch.Match(query, [sonst, exact, inTitle], KeineNoten, "uber wolken");
		Assert.Equal(0, matches[0].Rank);
		Assert.Same(exact, matches[0].Lied);
		Assert.Equal(1, matches[1].Rank);
		Assert.Same(inTitle, matches[1].Lied);
		Assert.Equal(2, matches[2].Rank);
		Assert.Same(sonst, matches[2].Lied);
	}

	[Fact]
	public void EqualRanksAreOrderedByTitleThenId()
	{
		var erster = Lied("Mond");
		var zweiter = Lied("mond");
		// The id decides what the title fold cannot: Mond from Mond; the
		// expected order follows the ids, not the creation order or the title.
		var matches = LiedSearch.Match(LiedSearch.TokensOf("mond"), [zweiter, erster], KeineNoten, "mond");
		var (kleiner, groesser) = erster.Id.CompareTo(zweiter.Id) < 0 ? (erster, zweiter) : (zweiter, erster);
		Assert.Equal([kleiner.Id, groesser.Id], matches.Select(m => m.Lied.Id).ToArray());
	}

	[Fact]
	public void AnEmptyQueryListsEveryLiedInTitleOrder()
	{
		var spater = Lied("Z");
		var fruh = Lied("A");
		var matches = LiedSearch.Match(LiedSearch.TokensOf(""), [spater, fruh], KeineNoten, "");
		Assert.All(matches, m => Assert.Equal(2, m.Rank));
		Assert.Equal([fruh.Id, spater.Id], matches.Select(m => m.Lied.Id).ToArray());
	}

	[Fact]
	public void PagingTakesTheRequestedWindowOnly()
	{
		var rows = Enumerable.Range(0, 3).Select(i => Lied($"Lied {i}")).ToList();
		var matches = LiedSearch.Match(LiedSearch.TokensOf(""), rows, KeineNoten, "");
		Assert.Equal(2, LiedSearch.Page(matches, 1, 2).Count);
		Assert.Single(LiedSearch.Page(matches, 2, 2));
		Assert.Empty(LiedSearch.Page(matches, 3, 2));
	}
}
