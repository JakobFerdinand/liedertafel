// Offline public synthetic input only. Link the unchanged frozen backend assembly
// from an external net10.0 console project; never modify that assembly/source.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Backend.Extraction;

var cases = new List<(string Name, string Text, int Repeat, string Suffix)>();
void Add(string name,string text,int repeat=1,string suffix="")=>cases.Add((name,text,repeat,suffix));
Add("review-voice-underscore","Tenor_1");
Add("review-voice-digit","Tenor12");
Add("review-metronome-letter","Andante = 72b");
Add("review-credit-digit","1Text: Dichter");
Add("review-pair-window","la ",11,"3 4");
Add("review-prefix-utf16","😀",300," = 72");
foreach(var text in new[]{"Tenor Bass","Tenor 1 Bass 2","Tenor 1,Tenor 2","1.Tenor/2.Bass","Tenor1Bass","Tenor12 Bass","TenorII_ Bass","éTenor","١Tenor","_Tenor","ⅠTenor","Tenoré","Tenor١","Tenor1é","😀Tenor","𝔄Tenor","Tenor\u0301","Tenor 1_ Bass"})Add("voice-"+cases.Count,text);
foreach(var text in new[]{"Lied F-Dur_","Lied 2F-Dur","Lied éF-Dur","Lied F-Duré","Lied F-Dur1","Lied F-Durβ","Lied F-Dur\u0301","Lied 😀F-Dur","Lied d-Moll_","Lied C-DurG-Dur"})Add("key-"+cases.Count,text);
foreach(var text in new[]{"_Musik: Komponist","éText: Dichter","Text: Eins,Musik: Zwei","Text: NameText: Behält","ⅠText: Dichter","😀Text: Dichter","Text: Künstler"})Add("credit-"+cases.Count,text);
Add("pair-in-window","la ",10,"3 4");
Add("stacked-outside-window","la ",12,"34");
Add("stacked-in-window","la ",11,"34");
foreach(var text in new[]{"=72_","=72β","=720a","=1234 =72","=241 =72","=72٢٣ =73"})Add("tempo-"+cases.Count,text);
Add("utf16-prefix-short-digits","😀",299,"=72");
Add("utf16-prefix-complete-digits","😀",298," =72");
Add("utf16-prefix-exact-ascii","a",597,"=72");
Add("utf16-prefix-cut-ascii","a",598,"=72");
Add("credit-rejected-outer-valid-inner","aText und Musik: Komponist");
Add("credit-rejected-outer-valid-inner-2","éMusik und Text: Dichter");

Console.Error.WriteLine($"Frozen analyzer MVID {typeof(ScoreTextAnalyzer).Assembly.ManifestModule.ModuleVersionId}");
var rows=cases.Select(c=>{
 var input=string.Concat(Enumerable.Repeat(c.Text,c.Repeat))+c.Suffix;
 var result=ScoreTextAnalyzer.Analyze(input);
 return new {c.Name,c.Text,c.Repeat,c.Suffix,CleanTextSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.CleanText))).ToLowerInvariant(),result.Facts};
});
Console.WriteLine(JsonSerializer.Serialize(rows,new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
