// Literal frozen BCL expressions / AiOptions thresholds; offline only.
using Archive.Backend.Ai;
using System.Globalization;
using System.Text.Json;

foreach (var value in new[] { "0.005", "0.025", "0.015", "1.005", "12.345", "0.0049", "0.0051", "-0.005", "-0.025", "-1.005", "-0.0001", "0", "15" })
{
	var amount=decimal.Parse(value,CultureInfo.InvariantCulture);
	Console.WriteLine(JsonSerializer.Serialize(new { kind="format", value, micro=(long)(amount*1_000_000m), expected=amount.ToString("0.00",CultureInfo.InvariantCulture) }));
}
var spellings = new[] {
	"5+", "+5", " 5+ ", "5 +", "5\t+", "5\u0085+", "5\u00a0+", "5-", "-5", "+-5", "5++", "-5-", "(5)", "5e0", "5_0",
	"5.0", "5.", ".5", ".", "1.2.3", "1,234.50", "1,,2", ", ,", "\t+5 ", "\u00a05+\u00a0", "0005", "0", "0+", "-0", "5\0", "5\0\0",
	"79228162514264337593543950335", "79228162514264337593543950336", "0.00000000000000000000000000005", "0.00000000000000000000000000015", "1.23456789012345678901234567895", "5,bloss,7", "15,15", "5\n-", "5- ", "5 + ", "+ 5", "5.5+", ".5-",
	"79228162514264337593543950335.4", "79228162514264337593543950335.5", "7.92281625142643375935439503355", "0.00000000000000000000000000005001", "0.00000000000000000000000000025", "1.23456789012345678901234567885", "0000000000000000000000000000005.00", "5\0+", "5+\0", "5\0 \0", "5\0x", "", " \t ", "\0"
};
var signWhiteMatrix = from number in new[] { "5", "5.", ".5" }
	from leading in new[] { "", "+", "-" }
	from whitespace in new[] { "", " ", "\t", "\r\n", "\u00a0", "\u0085" }
	from trailing in new[] { "", "+", "-" }
	select leading+number+whitespace+trailing;
foreach (var value in spellings.Concat(signWhiteMatrix).Distinct())
{
	var options=new AiOptions { NoticeThresholdsEur=value };
	var entries=options.ParseThresholds();
	var levels=entries.Where(e=>e.Usable).Select(e=>e.Amount).ToList();if (!levels.Contains(15m)) levels.Add(15m);levels=levels.Order().Where(v=>v>0).ToList();
	Console.WriteLine(JsonSerializer.Serialize(new { kind="threshold", value, entries=entries.Select(e=>new { entry=e.Entry, amount=e.Amount.ToString(CultureInfo.InvariantCulture), usable=e.Usable }), levels=levels.Select(v=>v.ToString(CultureInfo.InvariantCulture)), errors=entries.Where(e=>!e.Usable).Select(e=>e.Entry) }));
}
