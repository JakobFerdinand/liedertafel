using System.Globalization;

namespace Archive.Backend.Events;

/// <summary>
/// Culture-invariant German rendering of the explicit ARC-024 date model.
/// Month names and formats are hardcoded (no locale-dependent
/// <c>ToString</c>) so server-rendered display is identical across hosts and
/// tests. Approximation is kept honest: "um " marks approximate year/month
/// entries, "ca. " approximate day entries, and unknown dates always read
/// "Datum unbekannt", approximate or not.
/// </summary>
public static class EventDate
{
	public const string UnknownDisplay = "Datum unbekannt";

	private static readonly string[] MonthNames =
	[
		"", "Januar", "Februar", "März", "April", "Mai", "Juni",
		"Juli", "August", "September", "Oktober", "November", "Dezember",
	];

	public static string Display(int? year, int? month, int? day, bool approximate)
	{
		if (year is null)
			return UnknownDisplay;
		if (month is null || month is < 1 or > 12)
			return approximate ? $"um {Text(year.Value)}" : Text(year.Value);
		if (day is null)
			return $"{(approximate ? "um " : string.Empty)}{MonthNames[month.Value]} {Text(year.Value)}";
		return $"{(approximate ? "ca. " : string.Empty)}{Text(day.Value)}. {MonthNames[month.Value]} {Text(year.Value)}";
	}

	private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
