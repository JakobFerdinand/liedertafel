// Offline actual frozen SDK + AIJsonUtilities/estimator differential extension.
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Archive.Backend.Ai;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.AI;
using M=Microsoft.Extensions.AI.ChatMessage;
using C=Microsoft.Extensions.AI.ChatOptions;

foreach (var (name,text) in new[] {
	("review", "Grüß😀\u2028\u2029\u0085\uE000"), ("german", "Grüß äöü ÄÖÜ ß"), ("nonbmp", "😀𝄞"),
	("separators", "\u2028\u2029\u0085"), ("private-use", "\uE000\uF8FF"), ("controls", "\0\b\t\n\f\r\u001F\u007F\u009F"),
	("html-punctuation", "<>&+'\"\\/`"), ("other-spaces", "\u00A0\u1680\u2000\u202F\u205F\u3000"),
	("letters", "Ω Ж 漢 é é"), ("format-unassigned", "\u200B\u200C\u200D\uFEFF\u0378\uFFFF")
})
{
	var payload=new Dictionary<string,object?> { ["text"]=text };
	var serialized=JsonSerializer.Serialize(payload,AIJsonUtilities.DefaultOptions);
	var argsMessage=new M(ChatRole.Assistant,[new FunctionCallContent("call","f",payload)]);
	var resultMessage=new M(ChatRole.Tool,[new FunctionResultContent("call",payload)]);
	Console.WriteLine(JsonSerializer.Serialize(new { kind="payload",name,text,serialized,argumentsEstimate=AiTokenEstimate.TextInput([argsMessage],new C()),resultEstimate=AiTokenEstimate.TextInput([resultMessage],new C()) }));
}

const string prefix="data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Grüß😀\"},\"finish_reason\":null}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n";
foreach(var scenario in new[]{"prefix-malformed","bad-total","null-total","overflow-total","missing-total","empty-usage","null-usage","stream-bad-total","stream-null-total","stream-overflow-total","prefix-bad-total"})
{
	var streaming=scenario.StartsWith("stream-")||scenario.StartsWith("prefix-");
	var total=scenario.Contains("null-total")?"null":scenario.Contains("overflow-total")?"2147483648":"\"bad\"";
	var usage=scenario=="empty-usage"?"{}":scenario=="null-usage"?"null":scenario=="missing-total"?"{\"prompt_tokens\":5,\"completion_tokens\":2}":"{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":"+total+"}";
	var response=streaming?"data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[],\"usage\":"+usage+"}\n\ndata: [DONE]\n\n":"{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Antwort\"},\"finish_reason\":\"stop\"}],\"usage\":"+usage+"}";
	if(scenario=="prefix-malformed")response=prefix+"data: {bad\n\n";else if(scenario=="prefix-bad-total")response=prefix+response;
	var handler=new Handler(response,streaming);var updates=new List<object>();string? error=null;
	var client=Archive.Backend.Chat.AzureOpenAIChatClient.Create(new(){Provider="AzureOpenAI",Endpoint="https://offline.invalid/",DeploymentName="gpt-6-luna"},new Fake(),new AzureOpenAIClientOptions{Transport=new HttpClientPipelineTransport(new HttpClient(handler)),RetryPolicy=new ClientRetryPolicy(0)})!;
	try {
		if(streaming){await foreach(var update in client.GetStreamingResponseAsync([new M(ChatRole.User,"Frage")]))updates.Add(new {text=update.Text,usage=update.Contents.OfType<UsageContent>().Select(u=>new{input=u.Details.InputTokenCount,output=u.Details.OutputTokenCount})});}
		else{var r=await client.GetResponseAsync([new M(ChatRole.User,"Frage")]);updates.Add(new{text=r.Text,usage=r.Usage is null?null:new{input=r.Usage.InputTokenCount,output=r.Usage.OutputTokenCount}});}
	}catch(Exception e){error=e.GetType().Name;}
	Console.WriteLine(JsonSerializer.Serialize(new{kind="wire",scenario,streaming,response,updates,error}));
}
sealed class Fake:TokenCredential {
	public override AccessToken GetToken(TokenRequestContext c,CancellationToken t)=>new("offline-transport-228",DateTimeOffset.UtcNow.AddHours(1));
	public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c,CancellationToken t)=>new(GetToken(c,t));
}
sealed class Handler(string body,bool streaming):HttpMessageHandler {
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,streaming?"text/event-stream":"application/json")});
}
