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

	/// <summary>Derived date precision from the set components (ARC-024).</summary>
	public static string Precision(int? year, int? month, int? day)
		=> day is not null && month is not null ? "day"
			: month is not null ? "month"
			: year is not null ? "year"
			: "unknown";

	/// <summary>
	/// The one newest-first date order shared by the event list and the song
	/// history: known year descending; within a year day-precision entries
	/// first (month then day descending), then month-only, then year-only;
	/// unknown years last. 0 means equal dates; callers add their own
	/// deterministic tie-break.
	/// </summary>
	public static int CompareNewestFirst(
		int? leftYear, int? leftMonth, int? leftDay, int? rightYear, int? rightMonth, int? rightDay)
	{
		if (leftYear is null || rightYear is null)
		{
			if (leftYear == rightYear)
				return 0;
			return leftYear is null ? 1 : -1;
		}
		var yearOrder = rightYear.Value.CompareTo(leftYear.Value);
		if (yearOrder != 0)
			return yearOrder;
		var rankOrder = PrecisionRank(leftMonth, leftDay).CompareTo(PrecisionRank(rightMonth, rightDay));
		if (rankOrder != 0)
			return rankOrder;
		var monthOrder = (rightMonth ?? 0).CompareTo(leftMonth ?? 0);
		if (monthOrder != 0)
			return monthOrder;
		return (rightDay ?? 0).CompareTo(leftDay ?? 0);
	}

	/// <summary>0 = day precision, 1 = month only, 2 = year only.</summary>
	private static int PrecisionRank(int? month, int? day)
		=> month is null ? 2 : day is null ? 1 : 0;

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
