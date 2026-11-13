// Offline actual frozen programme model/matcher oracle; no credentials/provider.
// Build in an external project referencing the unchanged frozen backend.
using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Reading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Options = Microsoft.Extensions.Options.Options;
var settings = new AiOptions();
settings.Models["scripted"] = new AiModelPrice { InputPricePerMillionEur = 1m, OutputPricePerMillionEur = 1m };
var services = new ServiceCollection().BuildServiceProvider();
var gateway = new AiGateway(new Capture(), services, new Budget(), new Monitor(settings), new AiModels { Chat = "scripted" });
using var model = new ScanReadingModel(gateway, Options.Create(new ReadingOptions()), NullLoggerFactory.Instance);
await model.ReadProgrammAsync("Konzert\n14. Juni 1925\n1. Ave Maria", null, default);
await model.TranscribeProgrammAsync([new RenderedPage(1, [1, 2, 3], "image/jpeg")], null, default);
Console.WriteLine(JsonSerializer.Serialize(new { kind="prompts", fields=ScanPrompts.ProgrammFieldsInstructions, transcription=ScanPrompts.ProgrammTranscriptionInstructions }));
foreach (var text in new[] {" SÜSSER die, Glocken! ","Süßer die Glocken","Heidenröslein","Heidenroeslein","é e\u0301","İ I ı i", "ΟΣ ΟΣΑ Σ ς", "\u0345α\u0903\u20dd²Ⅻ１", "𐐀𐐨 𝟘 𠀀", "a😀b", "\u0000\u200b\u0085\u202f A\u00adB", "Ä A\u0308 ä", "ßẞ", "", "☃"})
 foreach(var bare in new[]{false,true}) Console.WriteLine(JsonSerializer.Serialize(new {kind="normalize",text,bare,value=ProgrammMatcher.Normalize(text,bare)}));
var titles = new[]{"Ave Maria","Ave Maria","Der Mond ist aufgegangen","Der Mond ist aufgegangn","Süßer die Glocken nie klingen","Wanderlust","Stille Nacht, heilige Nacht","O du fröhliche","Heidenröslein","Im Wald und auf der Heide","\uE000Ave Maria","𐐀Ave Maria"};
var lieder=titles.Select((title,i)=>new MatchableLied(Guid.Parse($"{i+1:x8}-0000-0000-0000-000000000000"),title)).ToList();
foreach(var title in titles.Concat(new[]{"Ave Mara","Ave","Der Mond ist aufgegang","SUESSER DIE GLOCKEN NIE KLINGEN!","Wanderlied","Stille Nacht","Heidenroeslein","Im Walde","","☃"}))
 Console.WriteLine(JsonSerializer.Serialize(new {kind="match",title,lieder,result=ProgrammMatcher.Match(title,lieder)}));
for(var cp=0;cp<=0x10ffff;cp++) {
 if(cp is >=0xd800 and <=0xdfff) continue;
 var scalar=char.ConvertFromUtf32(cp);
 var text="a"+scalar+"b";
 try {
  var full=ProgrammMatcher.Normalize(text);
  var bare=ProgrammMatcher.Normalize(text,true);
  var identity=cp<=0xffff?text:"a b";
  if(full!=identity||bare!=identity) Console.WriteLine(JsonSerializer.Serialize(new {kind="normalize-scalar",cp,full,bare}));
 } catch(ArgumentException) { Console.WriteLine(JsonSerializer.Serialize(new {kind="normalize-scalar",cp,error="ArgumentException"})); }
}
using(var bitmap=new SKBitmap(100,65)) {
 using(var canvas=new SKCanvas(bitmap)) {canvas.Clear(SKColors.Red);using var paint=new SKPaint {Color=SKColors.Blue};canvas.DrawRect(50,0,50,65,paint);}
 using var image=SKImage.FromBitmap(bitmap);
 foreach(var format in new[]{SKEncodedImageFormat.Jpeg,SKEncodedImageFormat.Png,SKEncodedImageFormat.Webp}) {
  using var data=image.Encode(format,95);
  var original=data.ToArray();
  foreach(var origin in format==SKEncodedImageFormat.Jpeg?Enumerable.Range(1,8):new[]{1}) {
   var bytes=original;
   if(format==SKEncodedImageFormat.Jpeg) {
    byte[] exif=[0xff,0xe1,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,(byte)origin,0,0,0,0,0,0,0];
    bytes=original[..2].Concat(exif).Concat(original[2..]).ToArray();
   }
   var method=typeof(ProgrammReader).GetMethod("Downscale",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
   var page=(RenderedPage)method.Invoke(null,[bytes,10,85])!;
   using var result=SKBitmap.Decode(page.Bytes);
   Console.WriteLine(JsonSerializer.Serialize(new {kind="photo",format=format.ToString(),origin,bytes=Convert.ToBase64String(bytes),width=result.Width,height=result.Height,topLeft=new[]{result.GetPixel(0,0).Red,result.GetPixel(0,0).Green,result.GetPixel(0,0).Blue},bottomRight=new[]{result.GetPixel(result.Width-1,result.Height-1).Red,result.GetPixel(result.Width-1,result.Height-1).Green,result.GetPixel(result.Width-1,result.Height-1).Blue}}));
  }
 }
}
foreach(var alpha in new byte[]{0,64}) {
 using var bitmap=new SKBitmap(100,65);using(var canvas=new SKCanvas(bitmap)) {canvas.Clear(SKColors.Transparent);using var paint=new SKPaint {Color=new SKColor(255,0,0,alpha)};canvas.DrawRect(0,0,100,65,paint);}
 using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,95);var bytes=data.ToArray();
 var method=typeof(ProgrammReader).GetMethod("Downscale",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;var page=(RenderedPage)method.Invoke(null,[bytes,10,85])!;using var result=SKBitmap.Decode(page.Bytes);
 Console.WriteLine(JsonSerializer.Serialize(new {kind="photo",format=$"AlphaPng{alpha}",origin=1,bytes=Convert.ToBase64String(bytes),width=result.Width,height=result.Height,topLeft=new[]{result.GetPixel(0,0).Red,result.GetPixel(0,0).Green,result.GetPixel(0,0).Blue},bottomRight=new[]{result.GetPixel(result.Width-1,result.Height-1).Red,result.GetPixel(result.Width-1,result.Height-1).Green,result.GetPixel(result.Width-1,result.Height-1).Blue}}));
}
var corpus=JsonDocument.Parse(File.ReadAllText("src/archive/backend-rs/tests/fixtures/231-programme-evaluation.json"));
var cleanTitle=typeof(ProgrammReader).GetMethod("CleanTitle",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
foreach(var text in new[]{"1. Ave Maria","１. Ave Maria","١. Ave Maria","𝟙. Ave Maria"}) Console.WriteLine(JsonSerializer.Serialize(new {kind="clean-title",text,value=cleanTitle.Invoke(null,[text])}));
var header=typeof(ProgrammReader).GetMethod("Header",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
foreach(var text in new[]{"𝟙05.2000","𝟙12.05.2000"}) {
 var read=new List<ReadField> {new("year","2000","sicher",text),new("month","5","sicher",text)};
 if(text.Contains("12")) read.Add(new("day","12","sicher",text));
 Console.WriteLine(JsonSerializer.Serialize(new {kind="header",text,value=header.Invoke(null,[read,ProgrammMatcher.Normalize(text)])}));
}
var costMethod=typeof(AiBudgetLedger).GetMethod("CostMicroEur",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
var price=new AiModelPrice {InputPricePerMillionEur=0.83m,OutputPricePerMillionEur=4.95m};
foreach(var doc in corpus.RootElement.GetProperty("documents").EnumerateArray()) {
 var transcription=doc.GetProperty("transcription").GetString()!;
 var prompt="PROGRAMMTEXT-ANFANG\n"+transcription+"\nPROGRAMMTEXT-ENDE\n";
 var answer=ScriptedScanReader.Programm([new ChatMessage(ChatRole.User,prompt)]);
 var input=(prompt.Length+3)/4;
 var output=(answer.Length+3)/4;
 var cost=(long)costMethod.Invoke(null,[price,1700L,120L])!;
 if(!string.IsNullOrWhiteSpace(transcription)) cost+=(long)costMethod.Invoke(null,[price,(long)input,(long)output])!;
 Console.WriteLine(JsonSerializer.Serialize(new {kind="scripted-cost",name=doc.GetProperty("name").GetString(),answer,input,output,cost}));
}
sealed class Capture : IChatClient {
 public object? GetService(Type t, object? key=null) => null;
 public void Dispose() { }
 public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options=null, CancellationToken cancellationToken=default) {
  var schema=(options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
  Console.WriteLine(JsonSerializer.Serialize(new {kind="request",schema,instructions=options?.Instructions,maxOutputTokens=options?.MaxOutputTokens,messages=messages.Select(m=>new{role=m.Role.ToString(),text=m.Text})}));
  return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,"{\"felder\":[],\"punkte\":[]}")){Usage=new UsageDetails{InputTokenCount=100,OutputTokenCount=20}});
 }
 public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, ChatOptions? o=null,CancellationToken c=default)=>throw new NotSupportedException();
}
sealed class Monitor(AiOptions options) : IOptionsMonitor<AiOptions> { public AiOptions CurrentValue=>options; public AiOptions Get(string? n)=>options;public IDisposable? OnChange(Action<AiOptions,string?> listener)=>null; }
sealed class Budget : IAiBudget {
 public Task<AiReservation> ReserveAsync(AiCall call,CancellationToken c)=>Task.FromResult(new AiReservation(Guid.NewGuid(),"2026-11",call.Feature,call.Model,1,call.EstimatedInputTokens,call.MaxOutputTokens));
 public Task SettleAsync(AiReservation r,AiUsage u)=>Task.CompletedTask;
 public Task<bool> WouldAdmitAsync(AiCall c,CancellationToken t)=>Task.FromResult(true);
 public Task<AiBudgetStatus> GetStatusAsync(CancellationToken c)=>Task.FromResult(new AiBudgetStatus("2026-11",0,15000000));
}
