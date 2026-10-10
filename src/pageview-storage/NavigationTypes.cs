namespace PageViewStorage;

/// <summary>The navigation types a browser reports and the writer accepts. A stored null means unclassified.</summary>
public static class NavigationTypes
{
	public const string Navigate = "navigate";
	public const string Reload = "reload";
	public const string BackForward = "back_forward";

	public static readonly IReadOnlyList<string> All = [Navigate, Reload, BackForward];

	public static bool IsKnown(string? value) => value is Navigate or Reload or BackForward;
}
