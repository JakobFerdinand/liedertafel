using System.Text.Json;
using Archive.Backend.Catalogue;
using Archive.Backend.Lieder;

namespace Archive.Backend.Tests;

/// <summary>
/// #192: the field catalog is the single declaration of the Lied's fields —
/// the camelCase wire names, the draft form names and every accessor derive
/// from it, and a field the browser sends that the catalog does not know is
/// collected, never dropped. The pinned name lists below are the wire
/// contract the browser builds; the same lists are pinned by the frontend
/// contract test <c>frontend/lib/lied-entwurf/felder.test.ts</c>.
/// </summary>
public sealed class FieldCatalogTests
{
	[Fact]
	public void WireNamesOfTheMembersApiMatchThePinnedContract()
	{
		// Mirror of the browser's full-Lied form; adding a field to the
		// catalog turns this red on purpose, so it can only be fixed together
		// with the browser contract test and the wire clients.
		Assert.Equal(
			["composer", "arranger", "lyricist", "voiceConfiguration", "musicalKey", "language", "lyrics", "occasion"],
			FieldCatalog.LiedFields.Select(f => f.Form).ToArray());
	}

	[Fact]
	public void DraftFormNamesMatchThePinnedContract()
	{
		// Mirror of the browser's Anlegen form (a title plus the fields the
		// draft carries; the Anlass is not part of it).
		Assert.Equal(
			["title", "composer", "arranger", "lyricist", "voiceConfiguration", "musicalKey", "language", "lyrics"],
			FieldCatalog.DraftFormNames);
	}

	[Fact]
	public void EveryCataloguedFieldBindsThroughCreateAndPatchRequests()
	{
		foreach (var field in FieldCatalog.LiedFields)
		{
			var body = $$"""{"{{field.Form}}":"{{field.Name}}-Wert"}""";
			var created = JsonSerializer.Deserialize<CreateLiedRequest>(body, WebOptions);
			Assert.Equal($"{field.Name}-Wert", created!.Value(field));
			var patched = JsonSerializer.Deserialize<PatchLiedRequest>(body, WebOptions);
			Assert.Equal($"{field.Name}-Wert", patched!.Value(field));
		}
	}

	[Fact]
	public void AFieldTheBrowserSendsButTheCatalogDoesNotKnowIsCollectedNotDropped()
	{
		var created = JsonSerializer.Deserialize<CreateLiedRequest>(
			"""{"composer":"x","instrumentation":"Trommeln"}""", WebOptions);
		Assert.Equal("x", created!.Value(FieldCatalog.LiedField("composer")));
		Assert.Equal(["instrumentation"], created.Unknown);

		var draft = JsonSerializer.Deserialize<EntwurfLiedBody>(
			"""{"title":"Ave Maria","occasion":"Konzert"}""", WebOptions);
		Assert.Equal("Ave Maria", draft!.Title);
		Assert.Equal(["occasion"], draft.Unknown);
	}

	[Fact]
	public void TheBindingKeepsTheOldWireCasingOfTitleAndRowVersion()
	{
		// The System.Text.Json binding the request records replace matched
		// the web options' case-insensitively; so does this one.
		var created = JsonSerializer.Deserialize<CreateLiedRequest>(
			"""{"Title":"Ave Maria","Composer":"x","Instrumentation":"Trommeln"}""", WebOptions);
		Assert.Equal("Ave Maria", created!.Title);
		Assert.Equal("x", created!.Value(FieldCatalog.LiedField("composer")));
		Assert.Equal(["Instrumentation"], created.Unknown);

		var patched = JsonSerializer.Deserialize<PatchLiedRequest>(
			"""{"RowVersion":3}""", WebOptions);
		Assert.Equal(3u, patched!.RowVersion);
		Assert.Empty(patched.Unknown);
	}

	[Fact]
	public void ARequestWithoutTheRowVersionKeyCarriesNoRowVersion()
	{
		var created = JsonSerializer.Deserialize<CreateLiedRequest>(
			"""{"title":"Ave Maria","rowVersion":3}""", WebOptions);
		Assert.Equal("Ave Maria", created!.Title);
		Assert.Equal(["rowVersion"], created.Unknown);

		var patched = JsonSerializer.Deserialize<PatchLiedRequest>(
			"""{"title":"Ave Maria","rowVersion":3}""", WebOptions);
		Assert.Equal(3u, patched!.RowVersion);
		Assert.Empty(patched.Unknown);
	}

	private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
}
