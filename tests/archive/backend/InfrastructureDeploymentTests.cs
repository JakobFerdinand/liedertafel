using System.Text.RegularExpressions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-021-1: the Bicep template and the shipped prices must name the same
/// deployment, otherwise the ledger refuses every chat run (<c>unpriced_model</c>)
/// after the infrastructure deploy.
/// </summary>
public sealed class InfrastructureDeploymentTests
{
	[Fact]
	public void TheDeploymentTheTemplateCreatesIsPricedByTheShippedConfiguration()
	{
		var bicep = File.ReadAllText(Path.Combine(ShippedConfiguration.RepositoryRoot(), "infrastructure", "archive", "main.bicep"));

		var parameter = Regex.Match(bicep, @"^param aiChatDeploymentName string = '([^']+)'", RegexOptions.Multiline);
		Assert.True(parameter.Success, "param aiChatDeploymentName with a literal default not found");
		// The deployment resource takes its name from that parameter and from nothing else.
		var resource = Regex.Match(bicep,
			@"^resource \w+ 'Microsoft\.CognitiveServices/accounts/deployments@[^']+' = \{\s*parent: \w+\s*name: aiChatDeploymentName\s*$",
			RegexOptions.Multiline);
		Assert.True(resource.Success, "the chat deployment resource must be named by aiChatDeploymentName");

		Assert.NotNull(ShippedConfiguration.Prices().FindPrice(parameter.Groups[1].Value));
	}
}
