// Actual unchanged ReadingOptions + ConfigurationBuilder/Get binding.
// External framework-only project links the frozen class; no provider/SQL.
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Archive.Backend.Reading;
string[] names = ["MaxPdfBytes","MaxPagesPerRequest","ImagesPerCall","MaxImageLongSide","JpegQuality","EstimatedTokensPerImage","TranscriptionMaxOutputTokens","FieldsMaxOutputTokens","MaxFieldTextCharacters","MaxAttempts","MaxDispatchPerRun","BatchSize","MaxMessagesPerRun"];
Dictionary<string,long> Snapshot(ReadingOptions o) => names.ToDictionary(n=>n,n=>Convert.ToInt64(typeof(ReadingOptions).GetProperty(n)!.GetValue(o)));
string[] spellings = [" 1 ","\t+2\r\n","\u00a01\u3000","0"," 0 ","-1","-0","+1","4","5","10","11","100","101","255","256","1600","2048","2049","2147483647","2147483648","-2147483648","-2147483649","9223372036854775807","9223372036854775808","-9223372036854775808","-9223372036854775809","18446744073709551615","","1_0","1+","--1","1.0","1e1","1 2","0x1","#1","&h1","0xffffffff","0x7fffffffffffffff","0xffffffffffffffff"];
var rows = new List<object>();
foreach(var name in names) foreach(var input in spellings) {
 var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Archive:Reading:"+name,input}}).Build();
 try {var value=config.GetSection("Archive:Reading").Get<ReadingOptions>()!;rows.Add(new{name,input,error=false,expected=Snapshot(value)});}
 catch(InvalidOperationException ex) {rows.Add(new{name,input,error=true,exception=ex.GetType().Name,expected=Snapshot(new ReadingOptions())});}
}
Console.WriteLine(JsonSerializer.Serialize(new {runtime=Environment.Version.ToString(),source="unchanged ReadingOptions + ConfigurationBuilder.Get",defaults=Snapshot(new ReadingOptions()),rows}));
