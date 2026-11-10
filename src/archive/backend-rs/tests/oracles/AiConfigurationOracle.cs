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

var cases = new (string Name, Dictionary<string,string> Env, string Model)[] {
	("defaults", new(), "gpt-6-luna"),
	("canonical-zero-cap", new() { ["Archive__Ai__MonthlyCapEur"]="0" }, "gpt-6-luna"),
	("upper-zero-cap", new() { ["ARCHIVE__AI__MONTHLYCAPEUR"]="0" }, "gpt-6-luna"),
	("mixed-lower-cap", new() { ["aRcHiVe__aI__mOnThLyCaPeUr"]="0.000001" }, "gpt-6-luna"),
	("upper-input-bound", new() { ["ARCHIVE__AI__MAXINPUTTOKENSPERCALL"]="1" }, "gpt-6-luna"),
	("lower-output-bound", new() { ["archive__ai__maxoutputtokenspercall"]="1" }, "gpt-6-luna"),
	("canonical-prices", new() { ["Archive__Ai__MaxOutputTokensPerCall"]="1", ["Archive__Ai__Models__gpt-6-luna__InputPricePerMillionEur"]="4", ["Archive__Ai__Models__gpt-6-luna__OutputPricePerMillionEur"]="9" }, "gpt-6-luna"),
	("mixed-price-fields-deployment-identity", new() { ["archive__AI__maxoutputtokenspercall"]="1", ["ARCHIVE__ai__MODELS__GPT-6-LUNA__inputpricepermillioneur"]="4", ["Archive__AI__models__GpT-6-LuNa__OUTPUTPRICEPERMILLIONEUR"]="9" }, "GpT-6-LuNa"),
	("zero-price", new() { ["archive__ai__models__GPT-6-LUNA__inputpricepermillioneur"]="0" }, "gpt-6-luna"),
	("negative-price", new() { ["ARCHIVE__AI__MODELS__gpt-6-luna__OUTPUTPRICEPERMILLIONEUR"]="-1" }, "gpt-6-luna"),
	("invalid-price", new() { ["ARCHIVE__AI__MODELS__GPT-6-LUNA__INPUTPRICEPERMILLIONEUR"]="invalid" }, "gpt-6-luna"),
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
