// Independent .NET 10 BCL + unchanged frozen assembly observations. No Rust calls.
// Run in an external project per docs/events-review-remediation.md.
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

var dir=Environment.GetEnvironmentVariable("ARCHIVE_FROZEN_TEST_BIN")!;
AssemblyLoadContext.Default.Resolving+=(_,n)=>File.Exists($"{dir}/{n.Name}.dll")?AssemblyLoadContext.Default.LoadFromAssemblyPath($"{dir}/{n.Name}.dll"):null;
var tests=AssemblyLoadContext.Default.LoadFromAssemblyPath($"{dir}/Archive.Backend.Tests.dll");
var backend=AssemblyLoadContext.Default.LoadFromAssemblyPath($"{dir}/Archive.Backend.dll");
const string d="12345678-abcd-4321-89ab-cdef01234567";
const string x="{0x12345678,0xabcd,0x4321,{0x89,0xab,0xcd,0xef,0x01,0x23,0x45,0x67}}";
var inputs=new List<string>{d,d.ToUpperInvariant(),d.Replace("-",""),"{"+d+"}","("+d+")",x,"", " ","urn:uuid:"+d,"URN:UUID:"+d,"("+d.Replace("-","")+")","({"+d+"})","{"+d.Replace("-","")+"}","(("+d+"))","{"+d+")","("+d+"}","("+d+" )","( "+d+")",d+"x",new string('0',36),"00000000-0000-0000-0000-000000000000"};
foreach(var ws in new[]{" ","\t","\r\n","\u0085","\u00a0","\u1680","\u2003","\u2028","\u202f","\u205f","\u3000","\u001c","\u200b","\ufeff"}) {inputs.Add(ws+d+ws);inputs.Add(x.Replace(",",ws+","+ws));}
foreach(var value in new[]{"0","1","0000000000000000000000001","ffffffff","100000000","+1","+0x1","0x1","0X1","+","0x","+0x","-1","g","0x+1","++1"})
 foreach(var index in new[]{0,1,2,3}) {var values=new[]{"12345678","abcd","4321","89"};values[index]=value;inputs.Add($"{{0x{values[0]},0x{values[1]},0x{values[2]},{{0x{values[3]},0xab,0xcd,0xef,0x01,0x23,0x45,0x67}}}}");}
foreach(var value in new[]{"+2345678","0x345678","+0x45678"})inputs.Add(value+d[8..]);
foreach(var value in new[]{"+bcd","0xcd","+0xd"})foreach(var start in new[]{9,14,19,24,28})inputs.Add(d[..start]+value+d[(start+4)..]);
foreach(var bad in new[]{x.Replace("0x89","0x100"),x.Replace("0xabcd","0x10000"),x.Replace("0x4321","0xffffffff"),x.Replace("0xabcd","0x100000000"),x.Replace("0x89,",""),x.Replace("0x89,","0x89,0x01,"),x+"}",x[..^1],x.Replace("0x89","89"),x.Replace("0x12345678","12345678")})inputs.Add(bad);
var guid=inputs.Distinct(StringComparer.Ordinal).Select(input=>new {input,parsed=Guid.TryParse(input,out var id)?id.ToString():null}).ToList();
File.WriteAllText(args[0]+"/guid-bcl.json",JsonSerializer.Serialize(guid));

var ht=tests.GetType("Archive.Backend.Tests.LiedTestHost",true)!;var host=Activator.CreateInstance(ht,new object?[5])!;
try {
 var service=(IServiceProvider)ht.GetProperty("Services")!.GetValue(host)!;using var scope=service.CreateScope();var db=scope.ServiceProvider.GetRequiredService(backend.GetType("Archive.Backend.Data.ArchiveDbContext",true)!);
 object Entity(string type,params(string,object?)[] fields){var t=backend.GetType(type,true)!;var e=Activator.CreateInstance(t)!;foreach(var (name,value) in fields)t.GetProperty(name)!.SetValue(e,value);db.GetType().GetMethod("Add",new[]{typeof(object)})!.Invoke(db,new[]{e});return e;}
 async Task Save()=>await(Task<int>)db.GetType().GetMethod("SaveChangesAsync",new[]{typeof(CancellationToken)})!.Invoke(db,new object[]{CancellationToken.None})!;
 Guid Id(int prefix,int n)=>Guid.Parse($"{prefix:00000000}-0000-0000-0000-{n:000000000000}");
 var pairs=new[]{("ä","a\u0308"),("co\u00adoperate","cooperate"),("ab\u200d","ab"),("Å","\u212b"),("a\u034f","a"),("a","A"),("e","é"),("ae","æ")};
 var results=new List<object>();var now=DateTimeOffset.Parse("2026-11-10T00:00:00Z");var member=await(Task<string>)ht.GetMethod("MemberAsync")!.Invoke(host,null)!;
 for(int group=0;group<pairs.Length;group++){
  var (left,right)=pairs[group];int offset=(group+1)*100;var ev=Id(1,offset);var common=Id(2,offset);var programme=Id(4,offset);var item=Id(5,offset);
  Entity("Archive.Backend.Events.ChoirEvent",("Id",ev),("Kind","concert"),("Title","Culture"),("CreatedAt",now),("UpdatedAt",now));
  Entity("Archive.Backend.Lieder.Lied",("Id",common),("Title","Tie Lied"),("CreatedAt",now),("UpdatedAt",now));
  Entity("Archive.Backend.Events.EventProgramme",("Id",programme),("EventId",ev),("CreatedAt",now),("UpdatedAt",now));
  Entity("Archive.Backend.Events.ProgrammeItem",("Id",item),("ProgrammeId",programme),("LiedId",common),("Position",1));
  var titles=new[]{left,right,left,right,right,left};var seconds=new[]{1,0,0,0,1,0};
  for(int i=1;i<=6;i++){
   var song=Id(2,offset+i);var recording=Id(6,offset+i);
   Entity("Archive.Backend.Lieder.Lied",("Id",song),("Title",titles[i-1]),("CreatedAt",now),("UpdatedAt",now));
   Entity("Archive.Backend.Lieder.Datei",("Id",Id(3,offset+i)),("LiedId",song),("RecordedAtEventId",ev),("Kind","aufnahme"),("ContentType","audio/mpeg"),("BlobName",$"dateien/culture-{offset+i}"),("CreatedAt",now.AddSeconds(seconds[i-1])));
   Entity("Archive.Backend.Recordings.Recording",("Id",recording),("EventId",ev),("Kind","audio"),("Label",titles[i-1]),("PublishedAt",now),("CreatedAt",now),("UpdatedAt",now));
   Entity("Archive.Backend.Recordings.RecordingPassage",("Id",Id(7,offset+i)),("RecordingId",recording),("ProgrammeItemId",item),("EventId",ev),("StartSeconds",1d),("EndSeconds",5d),("CreatedAt",now),("UpdatedAt",now));
  }
  await Save();
  var task=(Task)backend.GetType("Archive.Backend.Events.EventEndpoints",true)!.GetMethod("LoadLiedAufnahmenAsync")!.Invoke(null,new[]{db,(object)ev,CancellationToken.None})!;await task;
  var files=JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result")!.GetValue(task));
  var history=await(Task<JsonElement>)ht.GetMethod("GetJsonAsync")!.Invoke(host,new object[]{$"/api/lieder/{common}/historie",member})!;
  int N(JsonElement e,string key)=>int.Parse(e.GetProperty(key).GetString()![^12..],CultureInfo.InvariantCulture)-offset;
  results.Add(new{left,right,compare=Math.Sign(StringComparer.CurrentCulture.Compare(left,right)),fileOrder=files.EnumerateArray().Select(e=>N(e,"id")).ToArray(),passageOrder=history.GetProperty("auftritte")[0].GetProperty("recordings").EnumerateArray().Select(e=>N(e,"passageId")).ToArray(),ordinalDistinct=titles.Distinct(StringComparer.Ordinal).Count()});
 }
 File.WriteAllText(args[0]+"/culture-frozen.json",JsonSerializer.Serialize(results));
 File.WriteAllText(args[0]+"/provenance.json",JsonSerializer.Serialize(new{framework=RuntimeInformation.FrameworkDescription,bclMvid=typeof(Guid).Assembly.ManifestModule.ModuleVersionId,frozenMvid=backend.ManifestModule.ModuleVersionId,frozenSha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(backend.Location))),culture=CultureInfo.CurrentCulture.Name,sortVersion=CultureInfo.CurrentCulture.CompareInfo.Version.FullVersion,guidCount=guid.Count,cultureCount=results.Count}));
}finally{await((IAsyncDisposable)host).DisposeAsync();}
