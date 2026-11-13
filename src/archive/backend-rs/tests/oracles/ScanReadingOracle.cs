// Offline actual frozen scan agent/gateway request oracle. No real provider or
// credential exists. Build externally; never change frozen backend/tests.
using System.Text.Json;
using Archive.Backend.Ai;
using Archive.Backend.Reading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Options = Microsoft.Extensions.Options.Options;
var settings = new AiOptions();
settings.Models["scripted"] = new AiModelPrice { InputPricePerMillionEur = 1m, OutputPricePerMillionEur = 1m };
var services = new ServiceCollection().BuildServiceProvider();
var client = new Capture();
var gateway = new AiGateway(client, services, new Budget(), new Monitor(settings), new AiModels { Chat = "scripted" });
using var model = new ScanReadingModel(gateway, Options.Create(new ReadingOptions()), NullLoggerFactory.Instance);
await model.ReadFieldsAsync("Waldfahrt\nVon Franz Schubert", ["composer", "lyrics"], null, default);
await model.TranscribeAsync([new RenderedPage(1, [1, 2, 3], "image/jpeg")], null, default);
Console.WriteLine(JsonSerializer.Serialize(new { kind="prompts",fields=ScanPrompts.FieldsInstructions,transcription=ScanPrompts.TranscriptionInstructions,version=ScanPrompts.FieldsVersion }));
sealed class Capture : IChatClient {
 public object? GetService(Type t, object? key=null) => null;
 public void Dispose() { }
 public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options=null, CancellationToken cancellationToken=default) {
  var schema=(options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
  Console.WriteLine(JsonSerializer.Serialize(new {kind="request",schema,instructions=options?.Instructions,maxOutputTokens=options?.MaxOutputTokens,messages=messages.Select(m=>new{role=m.Role.ToString(),text=m.Text})}));
  return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,"{\"felder\":[]}")){Usage=new UsageDetails{InputTokenCount=100,OutputTokenCount=20}});
 }
 public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> m, Microsoft.Extensions.AI.ChatOptions? o=null,CancellationToken c=default)=>throw new NotSupportedException();
}
sealed class Monitor(AiOptions options) : IOptionsMonitor<AiOptions> { public AiOptions CurrentValue=>options; public AiOptions Get(string? n)=>options;public IDisposable? OnChange(Action<AiOptions,string?> listener)=>null; }
sealed class Budget : IAiBudget {
 public Task<AiReservation> ReserveAsync(AiCall call,CancellationToken c)=>Task.FromResult(new AiReservation(Guid.NewGuid(),"2026-11",call.Feature,call.Model,1,call.EstimatedInputTokens,call.MaxOutputTokens));
 public Task SettleAsync(AiReservation r,AiUsage u)=>Task.CompletedTask;
 public Task<bool> WouldAdmitAsync(AiCall c,CancellationToken t)=>Task.FromResult(true);
 public Task<AiBudgetStatus> GetStatusAsync(CancellationToken c)=>Task.FromResult(new AiBudgetStatus("2026-11",0,15000000));
}
