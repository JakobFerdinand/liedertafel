// Offline .NET configuration/BCL oracle. Link unchanged AiOptions.cs into an
// external net10 console project with Microsoft.AspNetCore.App; no provider.
using Archive.Backend.Ai;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

if (args.FirstOrDefault() == "config-child")
{
	try
	{
		var config = new ConfigurationBuilder()
			.AddJsonFile(args[1]).AddEnvironmentVariables().Build();
		var o = config.GetSection("Archive:Ai").Get<AiOptions>()!;
		object? Price(string model) => o.FindPrice(model) is { } p ? new {
			input = p.InputPricePerMillionEur.ToString(CultureInfo.InvariantCulture),
			output = p.OutputPricePerMillionEur.ToString(CultureInfo.InvariantCulture),
			costOneEachMicro = (long)Math.Ceiling(p.InputPricePerMillionEur+p.OutputPricePerMillionEur)
		} : null;
		Console.WriteLine(JsonSerializer.Serialize(new { error=false, cap=o.MonthlyCapEur.ToString(CultureInfo.InvariantCulture), input=o.MaxInputTokensPerCall, output=o.MaxOutputTokensPerCall, luna=Price("GpT-6-LuNa"), mixed=Price("mixed-test"), unknown=Price("unknown-deployment") }));
	}
	catch (InvalidOperationException) { Console.WriteLine("{\"error\":true}"); }
	return;
}

if (args.FirstOrDefault() == "decimal-matrix")
{
	// Actual DecimalConverter/configuration binding, NOT a replacement parser.
	// Float (leading sign/exponent, no trailing sign/grouping) differs from the
	// notice's Number style. Include raw properties even when FindPrice refuses.
	var values = new[] {
		"0.50000000000000000000000000005", "0.00000000000000000000000000005",
		"0.5000000000000000000000000000500001", "0.50000000000000000000000000015",
		"0.000000000000000000000000000050001", "0.00000000000000000000000000015",
		"0.00000000000000000000000000025", "-0.00000000000000000000000000005",
		"-0.50000000000000000000000000005", "-0.50000000000000000000000000015",
		"1.23456789012345678901234567885", "1.23456789012345678901234567895",
		"1.234567890123456789012345678850001", "9.99999999999999999999999999995",
		"0", "-0", "+0", "-0.0000", "0.0000000000000000000000000000000",
		"1", "1.2300", ".5", "5.", "+.5", "-.5", "+5", "-5", "0000000000000000000000000000005.00",
		"  +.5  ", "\t\r\n-0.00\v\f", "\u00a0+.5\u0085", "\u2000.5\u3000",
		"5+", "5-", "5 +", "+ 5", "5 5", "5\u00a0+", "\u200b5", "\uFEFF5",
		"1e0", "1E+1", "1e-1", "5e-29", "15e-29", "25e-29", "50000000000000000000000000005e-29",
		"1.2300e+2", "1.2300e-2", "-0.00e+2", "-0.00e-2", "0e+9999999999999999999999",
		"1e+10000", "1e-10000", "-1e-10000", "1e+9999999999999999999999", "1e-9999999999999999999999",
		"1e", "1e+", "1e-", "1e 1", "1e+ 1", "1ee1", "1e1.0", "1e1+", "1e\u00a01",
		"79228162514264337593543950335", "-79228162514264337593543950335",
		"79228162514264337593543950336", "-79228162514264337593543950336",
		"79228162514264337593543950335.4", "79228162514264337593543950335.5",
		"79228162514264337593543950334.5", "79228162514264337593543950334.50001",
		"7.9228162514264337593543950335e28", "7.9228162514264337593543950336e28",
		"79.228162514264337593543950335", "79.228162514264337593543950345",
		"1e28", "1e29", "1e-28", "1e-29", "1e00000000000000000000001",
		"1_0", "1,000", "1,5", "(5)", "$5", "0x10", "NaN", "Infinity", "", " ", ".", "+", "--5"
	};
	foreach (var target in new[] { "cap", "input", "output" })
	foreach (var (value, index) in values.Select((v, i) => (v, i)))
	{
		var field = target == "cap" ? "MonthlyCapEur" : $"Models:gpt-6-luna:{(target == "input" ? "Input" : "Output")}PricePerMillionEur";
		var entries = new Dictionary<string, string?> {
			["Archive:Ai:Models:gpt-6-luna:InputPricePerMillionEur"] = "0.5",
			["Archive:Ai:Models:gpt-6-luna:OutputPricePerMillionEur"] = "0.5",
			[$"Archive:Ai:{field}"] = value
		};
		object expected;
		try {
			var config = new ConfigurationBuilder().AddJsonFile(args[1]).AddInMemoryCollection(entries).Build();
			var o = config.GetSection("Archive:Ai").Get<AiOptions>()!;
			if (target != "cap" && !o.Models.ContainsKey("gpt-6-luna")) expected = new { error = true };
			else {
				var amount = target == "cap" ? o.MonthlyCapEur : target == "input" ? o.Models["gpt-6-luna"].InputPricePerMillionEur : o.Models["gpt-6-luna"].OutputPricePerMillionEur;
				long? capMicro = null;
				try { capMicro = checked((long)Math.Floor(o.MonthlyCapEur * 1_000_000m)); } catch (OverflowException) { }
				var bits = decimal.GetBits(amount);
				expected = new { error = false, amount = amount.ToString(CultureInfo.InvariantCulture), scale = (bits[3] >> 16) & 255, negative = bits[3] < 0, usable = o.FindPrice("gpt-6-luna") is not null, capMicro };
			}
		} catch (InvalidOperationException) { expected = new { error = true }; }
		Console.WriteLine(JsonSerializer.Serialize(new { name = $"decimal-{target}-{index}", target, value, expected }));
	}
	return;
}

var cases = new (string Name, Dictionary<string,string> Env, string Model)[] {
	("defaults", new(), "gpt-6-luna"),
	("canonical-zero-cap", new() { ["Archive__Ai__MonthlyCapEur"]="0" }, "gpt-6-luna"),
	("upper-zero-cap", new() { ["ARCHIVE__AI__MONTHLYCAPEUR"]="0" }, "gpt-6-luna"),
	("mixed-lower-cap", new() { ["aRcHiVe__aI__mOnThLyCaPeUr"]="0.000001" }, "gpt-6-luna"),
	("upper-input-bound", new() { ["ARCHIVE__AI__MAXINPUTTOKENSPERCALL"]="1" }, "gpt-6-luna"),
	("lower-output-bound", new() { ["archive__ai__maxoutputtokenspercall"]="1" }, "gpt-6-luna"),
	("canonical-prices", new() { ["Archive__Ai__MaxOutputTokensPerCall"]="1", ["Archive__Ai__Models__gpt-6-luna__InputPricePerMillionEur"]="4", ["Archive__Ai__Models__gpt-6-luna__OutputPricePerMillionEur"]="9" }, "gpt-6-luna"),
	("decimal-half-even-price", new() { ["Archive__Ai__Models__gpt-6-luna__InputPricePerMillionEur"]="0.50000000000000000000000000005", ["Archive__Ai__Models__gpt-6-luna__OutputPricePerMillionEur"]="0.5" }, "gpt-6-luna"),
	("decimal-underflow-unpriced", new() { ["Archive__Ai__Models__gpt-6-luna__InputPricePerMillionEur"]="0.00000000000000000000000000005", ["Archive__Ai__Models__gpt-6-luna__OutputPricePerMillionEur"]="0.5" }, "gpt-6-luna"),
	("mixed-price-fields-deployment-identity", new() { ["archive__AI__maxoutputtokenspercall"]="1", ["ARCHIVE__ai__MODELS__GPT-6-LUNA__inputpricepermillioneur"]="4", ["Archive__AI__models__GpT-6-LuNa__OUTPUTPRICEPERMILLIONEUR"]="9" }, "GpT-6-LuNa"),
	("zero-price", new() { ["archive__ai__models__GPT-6-LUNA__inputpricepermillioneur"]="0" }, "gpt-6-luna"),
	("negative-price", new() { ["ARCHIVE__AI__MODELS__gpt-6-luna__OUTPUTPRICEPERMILLIONEUR"]="-1" }, "gpt-6-luna"),
	("invalid-price", new() { ["ARCHIVE__AI__MODELS__GPT-6-LUNA__INPUTPRICEPERMILLIONEUR"]="invalid" }, "gpt-6-luna"),
	("invalid-input-price-underscore", new() { ["ARCHIVE__AI__MODELS__GPT-6-LUNA__INPUTPRICEPERMILLIONEUR"]="1_0" }, "gpt-6-luna"),
	("invalid-output-price-underscore", new() { ["archive__ai__models__gpt-6-luna__outputpricepermillioneur"]="1_0" }, "gpt-6-luna"),
	("invalid-cap-underscore", new() { ["archive__ai__monthlycapeur"]="1_5" }, "gpt-6-luna"),
	("invalid-cap", new() { ["archive__ai__monthlycapeur"]="invalid" }, "gpt-6-luna"),
	("unknown-price-field-ignored", new() { ["aRcHiVe__aI__MoDeLs__GPT-6-LUNA__NotAPrice"]="invalid" }, "gpt-6-luna"),
	("unknown-model-unpriced", new(), "unknown-deployment"),
	("unknown-model-only-one-price", new() { ["ARCHIVE__AI__MODELS__Unknown-Deployment__INPUTPRICEPERMILLIONEUR"]="4" }, "unknown-deployment"),
	("new-model-preserves-case", new() { ["ARCHIVE__AI__MODELS__MiXeD-Test__INPUTPRICEPERMILLIONEUR"]="4", ["archive__AI__models__mixed-test__outputpricepermillioneur"]="9" }, "MiXeD-Test"),
	("colon-normalization", new() { ["ARCHIVE:AI:MONTHLYCAPEUR"]="0" }, "gpt-6-luna"),
	("mixed-separators", new() { ["archive__AI:MaxInputTokensPerCall"]="1" }, "gpt-6-luna"),
};
foreach (var test in cases)
{
	var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput=true, RedirectStandardError=true };
	start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
	start.ArgumentList.Add("config-child"); start.ArgumentList.Add(args[0]);
	foreach (var key in start.Environment.Keys.Where(k => k.Replace("__", ":").StartsWith("Archive:Ai:",StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
	foreach (var (key,value) in test.Env) start.Environment[key]=value;
	using var child = Process.Start(start)!;
	var output = child.StandardOutput.ReadToEnd(); var error = child.StandardError.ReadToEnd(); child.WaitForExit();
	if (child.ExitCode != 0) throw new Exception($"oracle child failed: {test.Name}; {error}");
	Console.WriteLine(JsonSerializer.Serialize(new { name=test.Name, env=test.Env, model=test.Model, expected=JsonDocument.Parse(output).RootElement.Clone() }));
}
