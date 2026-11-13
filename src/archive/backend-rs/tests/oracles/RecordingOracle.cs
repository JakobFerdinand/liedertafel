// External probe only. Reference the unchanged Archive.Backend.dll and
// Microsoft.AspNetCore.App from a net10.0 console project. See recordings-parity.md.
using System.Text.Json;
using Archive.Backend.Recordings;
var options=new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
var types=new[]{typeof(CreateRecordingRequest),typeof(AttachDateiRequest),typeof(PatchRecordingRequest),typeof(CreatePassageRequest),typeof(PatchPassageRequest),typeof(DeletePassageRequest),typeof(ReviewPassagesRequest),typeof(ReviewPassageItem)};
Console.WriteLine(typeof(CreateRecordingRequest).Assembly.ManifestModule.ModuleVersionId);
foreach(var type in types) Console.WriteLine($"DTO {type.Name}: {string.Join(", ",type.GetProperties().Select(p=>$"{p.Name}:{p.PropertyType}"))}");
int count=0;
void Probe(Type type,string raw){try{var value=JsonSerializer.Deserialize(raw,type,options);var write=new JsonSerializerOptions(options){NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};Console.WriteLine($"{++count} {type.Name} {raw} ACCEPT {JsonSerializer.Serialize(value,type,write)}");}catch(JsonException){Console.WriteLine($"{++count} {type.Name} {raw} REJECT");}}
foreach(var raw in new[]{"1","1.0","1e0","\"1\"","\"+1\"","\" 1 \"","\"1e3\"","\"NaN\"","\"Infinity\"","\"-Infinity\"","1e400","null","true","\".5\"","\"1.\"","\"0x1\""}){
 Probe(typeof(PatchRecordingRequest),"{\"durationSeconds\":"+raw+"}");Probe(typeof(CreatePassageRequest),"{\"startSeconds\":"+raw+"}");}
foreach(var type in types) foreach(var raw in new[]{"{}","null","[]","42","{\"unknown\":1e999}","{\"expectedVersion\":\"01\"}"})Probe(type,raw);
foreach(var raw in new[]{"{\"durationSeconds\":false,\"durationSeconds\":1}","{\"LABEL\":\"first\",\"label\":\"last\"}","{\"downloadEnabled\":null}","{\"downloadEnabled\":1}","{\"expectedVersion\":\"+1\"}"})Probe(typeof(PatchRecordingRequest),raw);
foreach(var raw in new[]{"{\"passages\":[null]}","{\"passages\":[{}]}","{\"passages\":[{\"expectedVersion\":false}],\"PASSAGES\":[]}","{\"passages\":{},\"passages\":[]}","{\"passages\":[{\"id\":\"11111111111111111111111111111111\"}]}"})Probe(typeof(ReviewPassagesRequest),raw);
foreach(var text in new[]{"nan","NAN","+NaN","-NaN","inf","infinity","+Infinity","INFINITY","01","-.5","1.e2","1,2","\t1","1_0","1e400","-1e400"})Probe(typeof(PatchRecordingRequest),"{\"durationSeconds\":"+JsonSerializer.Serialize(text)+"}");
foreach(var number in new[]{1.2345,1.2355,0.0005,0.0015,1000.0005,172800.0005})Console.WriteLine($"ROUND {number:R} {Math.Round(number,3):R}");
Console.WriteLine($"OBSERVATIONS {count}");
