using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Chat;
using Archive.Backend.Reading;
using Microsoft.Extensions.Configuration;

namespace Archive.Backend.Inventur;

/// <summary>
/// #98 Quellen-Inventur as the console command <c>--inventur &lt;verzeichnis&gt;</c>.
/// Deterministic tooling (architecture §14): it walks a source folder export
/// without any database, queue or model and writes a JSON measurement report.
/// The model key is the reading model's price key: an explicit
/// <c>--model</c> override, otherwise <c>Archive:Chat:DeploymentName</c>,
/// otherwise the offline stand-in key — a key without a configured price keeps
/// the EUR figure empty instead of inventing one.
/// </summary>
public static class InventurCommand
{
	public static int Run(IReadOnlyList<string> arguments, TextWriter output)
	{
		var root = Positional(arguments);
		if (root is null || !Directory.Exists(root))
		{
			output.WriteLine(root is null
				? "Verzeichnis nicht angegeben. Usage: --inventur <verzeichnis> [--out <bericht.json>] [--model <modellschlüssel>]"
				: $"Verzeichnis nicht gefunden: {root}");
			output.WriteLine("Usage: --inventur <verzeichnis> [--out <bericht.json>] [--model <modellschlüssel>]");
			return 2;
		}

		var configuration = new ConfigurationBuilder()
			.SetBasePath(AppContext.BaseDirectory)
			.AddJsonFile("appsettings.json", optional: true)
			.AddJsonFile("appsettings.Development.json", optional: true)
			.AddEnvironmentVariables()
			.Build();
		var reading = new ReadingOptions();
		configuration.GetSection(ReadingOptions.SectionName).Bind(reading);
		var ai = new AiOptions();
		configuration.GetSection(AiOptions.SectionName).Bind(ai);
		var model = arguments.Option("--model", out var modelOverride)
			? modelOverride.Trim()
			: (configuration["Archive:Chat:DeploymentName"]?.Trim() is { Length: > 0 } deployment
				? deployment
				: ScriptedChatClient.ModelKey);

		var run = Inventur.Run(root, model, new InventurSettings(reading, ai));

		var json = JsonSerializer.Serialize(run, InventurJson.Options);
		if (arguments.Option("--out", out var report))
		{
			var directory = Path.GetDirectoryName(Path.GetFullPath(report));
			if (directory is not null)
				Directory.CreateDirectory(directory);
			File.WriteAllText(report, json);
			output.WriteLine($"Bericht geschrieben: {Path.GetFullPath(report)}");
		}
		else
			output.WriteLine(json);

		output.WriteLine();
		output.WriteLine($"Inventur von {Path.GetFullPath(root)}");
		output.WriteLine($"{run.Totals.FileCount} Dateien, {run.Totals.Bytes} Byte, {run.Totals.FolderCount} Ordner ({run.Totals.EmptyFolders.Count} leer)");
		if (run.Largest.Count > 0)
			output.WriteLine($"Größe der größten Datei: {run.Largest[0].Bytes} Byte ({run.Largest[0].Path})");
		output.WriteLine($"PDFs: {run.Pdf.Files}, mit eingebettetem Text {run.Pdf.EmbeddedText}, Scans {run.Pdf.Scanned} ({run.Pdf.ScannedPages} Seiten)");
		output.WriteLine($"Midis: {run.Media.MidiFiles}");
		output.WriteLine($"Namenskonflikte: {run.Duplicates.Count}");
		if (run.ReadingCost.EstimatedEur is { } eur)
			output.WriteLine($"Lesekosten-Schätzung ({run.ReadingCost.Model}, analytisch): {eur:0.000000} EUR für {run.ReadingCost.Calls} Anfragen");
		else
			output.WriteLine($"Lesekosten-Schätzung ({run.ReadingCost.Model}): kein Preis unter Archive:Ai:Models — analytisch, ohne Modellanfrage.");
		return 0;
	}

	/// <summary>The one positional argument: an <c>--&lt;option&gt; &lt;value&gt;</c> pair never takes its value as the directory.</summary>
	private static string? Positional(IReadOnlyList<string> arguments)
	{
		for (var index = 0; index < arguments.Count; index++)
		{
			var value = arguments[index];
			if (value is "--out" or "--model")
				index++;
			else if (value.StartsWith('-'))
				continue;
			else
				return value;
		}
		return null;
	}
}

/// <summary>The stable report shape: camelCase fields for the mapping consumer, indented, deterministic.</summary>
public static class InventurJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};
}

/// <summary>Positonal option lookup for the few command switches; no argument parser dependency.</summary>
internal static class InventurArguments
{
	public static bool Option(this IReadOnlyList<string> arguments, string name, out string value)
	{
		for (var index = 0; index < arguments.Count; index++)
		{
			if (string.Equals(arguments[index], name, StringComparison.Ordinal) && index + 1 < arguments.Count)
			{
				value = arguments[index + 1];
				return true;
			}
		}
		value = "";
		return false;
	}
}
