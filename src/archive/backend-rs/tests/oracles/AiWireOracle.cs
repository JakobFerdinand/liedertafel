// Offline oracle. Link this file and frozen Chat/AzureOpenAIChatClient.cs into
// an external net10 console project referencing frozen Archive.Backend.csproj.
// No sockets, real credentials or model calls. See docs/ai-foundation-parity.md.
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.AI;
using C = Microsoft.Extensions.AI.ChatOptions;
using M = Microsoft.Extensions.AI.ChatMessage;

foreach (var scenario in new[] { "plain", "stream", "streamMetadata", "streamFiltered", "filtered", "tools", "schema", "image", "retry429", "retry500", "exhaust429", "exhaust408", "exhaust409", "exhaust502", "exhaust503", "exhaust504", "noRetry400", "malformed", "zeroUsage", "missingUsage", "badUsage", "nullUsageFields", "oversizedUsage" })
{
	var handler = new Capture(scenario);
	var credential = new FakeCredential();
	var client = Archive.Backend.Chat.AzureOpenAIChatClient.Create(
		new() { Provider = "AzureOpenAI", Endpoint = "https://example.openai.azure.com/", DeploymentName = "gpt-6-luna" },
		credential,
		new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) })!;
	var options = new C { MaxOutputTokens = 200, Instructions = "Anweisung" };
	List<M> messages = [new(ChatRole.User, "Frage ü😀")];
	if (scenario == "tools")
	{
		options.Tools = [AIFunctionFactory.Create((string titel) => "Ergebnis", "suche", "Lied suchen")];
		messages.Add(new(ChatRole.Assistant, [new FunctionCallContent("call_1", "suche", new Dictionary<string, object?> { ["titel"] = "Grüß😀" })]));
		messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call_1", new { titel = "Grüß😀" })]));
	}
	if (scenario == "schema")
		options.ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"titel\":{\"type\":\"string\"}},\"required\":[\"titel\"],\"additionalProperties\":false}").RootElement, "felder", "Felder lesen");
	if (scenario == "image")
		messages = [new(ChatRole.User, [new TextContent("abc"), new DataContent(new byte[]{1,2,3}, "image/png")])];
	string outcome;
	try
	{
		if (scenario.StartsWith("stream"))
		{
			var updates = new List<object>();
			await foreach(var u in client.GetStreamingResponseAsync(messages, options))
				updates.Add(new { text = u.Text, usages = u.Contents.OfType<UsageContent>().Select(x => new { input=x.Details.InputTokenCount, output=x.Details.OutputTokenCount }) });
			outcome = JsonSerializer.Serialize(updates);
		}
		else
		{
			var r = await client.GetResponseAsync(messages, options);
			outcome = JsonSerializer.Serialize(new { text=r.Text, usage = r.Usage is null ? null : new { input=r.Usage.InputTokenCount, output=r.Usage.OutputTokenCount } });
		}
	}
	catch(Exception e) { outcome = e.GetType().Name; }
	Console.WriteLine(JsonSerializer.Serialize(new { scenario, handler.Requests, credential.Scopes, outcome }));
}

sealed class FakeCredential : TokenCredential
{
	public List<string[]> Scopes { get; } = [];
	public override AccessToken GetToken(TokenRequestContext c, CancellationToken t) { Scopes.Add(c.Scopes); return new("fake-offline-228", DateTimeOffset.UtcNow.AddHours(1)); }
	public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken t) => new(GetToken(c,t));
}
sealed class Capture(string scenario) : HttpMessageHandler
{
	public List<object> Requests { get; } = [];
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
	{
		var body = await request.Content!.ReadAsStringAsync(token);
		Requests.Add(new { uri=request.RequestUri!.ToString(), body=JsonDocument.Parse(body).RootElement.Clone() });
		var status = scenario.StartsWith("exhaust") ? int.Parse(scenario[7..]) : scenario switch { "retry429" when Requests.Count<3 => 429, "retry500" when Requests.Count<3 => 500, "noRetry400" => 400, _ => 200 };
		var stream = scenario.StartsWith("stream");
		var usage = scenario == "missingUsage" ? "" : scenario == "zeroUsage" ? ",\"usage\":{}" : ",\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}";
		var content = status != 200 ? "{\"error\":{\"code\":\"synthetic\",\"message\":\"private-error\"}}" : scenario == "malformed" ? "{bad" : stream
			? "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Antwort\"},\"finish_reason\":\"stop\"}]}\n\ndata: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\ndata: [DONE]\n\n"
			: "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Antwort\"},\"finish_reason\":\"stop\"}]"+usage+"}";
		if (scenario == "filtered" || scenario == "streamFiltered") content = content.Replace("\"stop\"", "\"content_filter\"");
		if (scenario == "badUsage") content = content.Replace("\"prompt_tokens\":5", "\"prompt_tokens\":\"bad\"");
		if (scenario == "nullUsageFields") content = content.Replace("\"prompt_tokens\":5", "\"prompt_tokens\":null");
		if (scenario == "oversizedUsage") content = content.Replace("\"prompt_tokens\":5", "\"prompt_tokens\":2147483648");
		if (scenario == "streamMetadata") content = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-6-luna\",\"choices\":[],\"prompt_filter_results\":[]}\n\n" + content;
		var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(content, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
		response.Headers.TryAddWithoutValidation("retry-after-ms", "1");
		return response;
	}
}
