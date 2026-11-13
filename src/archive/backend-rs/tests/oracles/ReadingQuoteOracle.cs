// Executes the unchanged frozen trust helper, not a reimplemented normalizer.
// External project references ../backend; offline, no provider/SQL/credentials.
using System.Reflection;
using System.Text.Json;
using Archive.Backend.Reading;
var normalize = typeof(ScoreFieldPrefill).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
var clean = typeof(ScoreFieldPrefill).GetMethod("CleanValue", BindingFlags.NonPublic | BindingFlags.Static)!;
string Normalize(string value) => (string)normalize.Invoke(null, [value])!;
var scalarChanges = new SortedDictionary<int, string>();
var scalarCount = 0;
for (int point = 0; point <= 0x10ffff; point++) {
 if (point is >= 0xd800 and <= 0xdfff) continue;
 var source = "x" + char.ConvertFromUtf32(point) + "x";
 var result = Normalize(source);
 if (result != source) scalarChanges.Add(point, result);
 scalarCount++;
}
var samples = new (string name, string field, string value, string quote, string text)[] {
 ("dotted-i-refuse", "composer", "i\u0307smail", "Von İsmail", "Von İsmail"),
 ("sigma-verify", "composer", "Οσ", "Von ΟΣ", "Von ΟΣ"),
 ("same-dotted-i", "composer", "İsmail", "Von İsmail", "Von İsmail"),
 ("dotless-is-not-i", "composer", "ısmail", "Von ISMAIL", "Von ISMAIL"),
 ("german-accents", "composer", "Müller Öß", "Von MÜLLER Öẞ", "Von MÜLLER Öẞ"),
 ("no-ss-fold", "composer", "groß", "Von GROSS", "Von GROSS"),
 ("no-ligature-fold", "composer", "ﬃ", "Von ffi", "Von ffi"),
 ("combining-preserved", "composer", "O\u0308zil", "Von O\u0308ZIL", "Von O\u0308ZIL"),
 ("no-nfc", "composer", "Özil", "Von O\u0308ZIL", "Von O\u0308ZIL"),
 ("supplementary-case", "composer", "\U00010428 Name", "Von \U00010400 NAME", "Von \U00010400 NAME"),
 ("supplementary-min-utf16", "composer", "😀", "😀😀", "😀😀"),
 ("quote-too-short", "composer", "Ö", "ÖÖÖ", "ÖÖÖ"),
 ("soft-hyphen", "composer", "Schubert", "Von Schu\u00adbert", "Von Schu\u00adbert"),
 ("whitespace", "composer", "Franz Schubert", "Von Franz Schubert", "Von\u0085FRANZ\u202fSCHUBERT\u3000"),
 ("feff-not-whitespace", "composer", "Franz Schubert", "Von Franz Schubert", "Von Franz\ufeffSchubert"),
 ("zero-width-not-whitespace", "composer", "Franz Schubert", "Von Franz Schubert", "Von Franz\u200bSchubert"),
 ("lyrics-verified", "lyrics", "Οσ singt\r\nGroß und schön", "ΟΣ singt Groß und schön", "ΟΣ singt\nGROẞ UND SCHÖN"),
 ("lyrics-invented", "lyrics", "Οσ singt\nErfundene Zeile", "ΟΣ singt Groß und schön", "ΟΣ singt\nGROẞ UND SCHÖN")
};
var quotes = samples.Select(s => new {s.name,s.field,s.value,s.quote,s.text,
 normalizedValue=Normalize(s.value),normalizedText=Normalize(s.text),
 accepted=ScoreFieldPrefill.QuoteIsVerified(new ReadField(s.field,s.value,"sicher",s.quote),s.value,s.text)}).ToArray();
var cleanSamples = new (string field, string raw)[] {
 ("composer", string.Concat(Enumerable.Repeat("😀",75))),
 ("composer", string.Concat(Enumerable.Repeat("😀",76))),
 ("composer", "\u00a0Müller\u3000"), ("composer", "zwei\r\nZeilen"),
 ("lyrics", new string('x',3500)), ("lyrics", new string('x',3501)),
 ("lyrics", "eine\r\nzweite Zeile"), ("composer", "\ufeffName\ufeff")
};
Console.WriteLine(JsonSerializer.Serialize(new {runtime=Environment.Version.ToString(),source="unchanged ScoreFieldPrefill.Normalize/CleanValue/QuoteIsVerified",scalarCount,scalarChanges,quotes,
 cleans=cleanSamples.Select(s=>new{s.field,s.raw,value=(string?)clean.Invoke(null,[s.field,s.raw])}).ToArray()}));
