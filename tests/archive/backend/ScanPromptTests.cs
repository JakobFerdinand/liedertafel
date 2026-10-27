using Archive.Backend.Catalogue;
using Archive.Backend.Reading;

namespace Archive.Backend.Tests;

/// <summary>
/// #192: the reading prompt's field list is the field catalog's, so a new
/// field joins the prompt without a second list, and the version says so.
/// </summary>
public sealed class ScanPromptTests
{
	[Fact]
	public void ThePromptKnowsEveryFieldTheCatalogGivesAPromptTo()
	{
		foreach (var field in FieldCatalog.LiedFields.Where(f => f.Prompt is not null))
		{
			Assert.Contains($"- {field.Name}: {field.Prompt!.Split('\n')[0]}", ScanPrompts.FieldsInstructions);
			foreach (var line in field.Prompt.Split('\n')[1..])
				Assert.Contains(line, ScanPrompts.FieldsInstructions);
		}
	}

	[Fact]
	public void ThePromptNamesOnlyCataloguedFieldsPlusTitleAndVoiceLabel()
	{
		var bullets = ScanPrompts.FieldsInstructions.Split('\n').Count(l => l.TrimStart().StartsWith("- "));
		Assert.Equal(
			FieldCatalog.LiedFields.Count(f => f.Prompt is not null) + 2,
			bullets);
	}

	[Fact]
	public void ThePromptVersionWasBumpedWithTheDerivedFieldList()
	{
		Assert.Equal("scan-fields-v3", ScanPrompts.FieldsVersion);
	}
}
