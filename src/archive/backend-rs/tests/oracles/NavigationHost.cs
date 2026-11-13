// Offline-only ACTUAL frozen Program/Kestrel + real PG/reader/Gateway/ledger.
// The sole replacement is keyed provider delivery, held by loopback HTTP. No
// Azure credential/provider construction, frontend route mock or frozen edit.
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace NavigationFixture;
public static class Entry {
 public static async Task Main(){
  var endpoint=new Uri(Environment.GetEnvironmentVariable("ARCHIVE_NAVIGATION_GATE")!);
  if(endpoint.Scheme!="http"||!endpoint.IsLoopback)throw new InvalidOperationException("offline gate requires loopback");
  var connection=Environment.GetEnvironmentVariable("ConnectionStrings__archive-db")!;
  if(!connection.Contains("Database=test230_navigation_dotnet_",StringComparison.Ordinal)||!connection.Contains("Host=127.0.0.1",StringComparison.Ordinal))throw new InvalidOperationException("offline fixture refuses nonlocal database");
  var factory=new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder=>{
   builder.UseEnvironment("Development");builder.UseContentRoot(Environment.GetEnvironmentVariable("ARCHIVE_FROZEN_CONTENT_ROOT")!);builder.UseWebRoot(Environment.GetEnvironmentVariable("ARCHIVE_STATIC_ROOT")!);
   builder.ConfigureTestServices(services=>{
    services.RemoveAllKeyed<IChatClient>(Archive.Backend.Ai.AiGateway.ProviderKey);
    services.AddKeyedSingleton<IChatClient>(Archive.Backend.Ai.AiGateway.ProviderKey,new HeldClient(endpoint));
   });
  });
  factory.UseKestrel(int.Parse(Environment.GetEnvironmentVariable("PORT")!));factory.StartServer();
  var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
  using var signal=PosixSignalRegistration.Create(PosixSignal.SIGTERM,c=>{c.Cancel=true;stopped.TrySetResult();});
  Console.WriteLine("Offline frozen navigation host ready");await stopped.Task;await factory.DisposeAsync();
 }
}
sealed class HeldClient(Uri endpoint):IChatClient {
 readonly Archive.Backend.Chat.ScriptedChatClient script=new();
 readonly HttpClient client=new();
 public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default){
  var updates=new List<ChatResponseUpdate>();await foreach(var update in GetStreamingResponseAsync(messages,options,cancellationToken))updates.Add(update);return updates.ToChatResponse();
 }
 public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,[EnumeratorCancellation]CancellationToken cancellationToken=default){
  var copy=messages.ToList();using var response=await client.PostAsJsonAsync(endpoint,new{transcription=Archive.Backend.Reading.ScriptedScanReader.IsTranscription(copy)},cancellationToken);response.EnsureSuccessStatusCode();
  await foreach(var update in script.GetStreamingResponseAsync(copy,options,cancellationToken))yield return update;
 }
 public object? GetService(Type serviceType,object? serviceKey=null)=>null;
 public void Dispose(){client.Dispose();script.Dispose();}
}
