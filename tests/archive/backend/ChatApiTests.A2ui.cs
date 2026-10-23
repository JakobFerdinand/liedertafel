using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using AiChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Archive.Backend.Tests;

/// <summary>
/// ARC-022-4: the chat shows archive records as A2UI cards. The model asks
/// through <c>zeige_oberflaeche</c> with record ids only; the server validates
/// the request against its catalog, resolves the ids to visibility-checked
/// data, drops everything else before streaming, stores what it showed with
/// the answer and re-checks visibility whenever the thread is loaded.
/// </summary>
public sealed partial class ChatApiTests
{
	private const string CatalogId = "urn:liedertafel:archiv:a2ui:katalog:v1";

	private const string A2uiEventName = "archive.a2ui";

	[Fact]
	public async Task CardQuestionStreamsAValidatedLiedKarteWithServerResolvedData()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige mir die Waldfahrt als Karte");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadAsStringAsync();
		var messages = CollectA2uiMessages(body);
		Assert.Equal(2, messages.Count);
		Assert.Equal("v0.9", messages[0].GetProperty("version").GetString());
		var create = messages[0].GetProperty("createSurface");
		Assert.Equal(CatalogId, create.GetProperty("catalogId").GetString());
		var surfaceId = create.GetProperty("surfaceId").GetString();
		var update = messages[1].GetProperty("updateComponents");
		Assert.Equal(surfaceId, update.GetProperty("surfaceId").GetString());
		var component = Assert.Single(update.GetProperty("components").EnumerateArray().ToList());
		Assert.Equal("root", component.GetProperty("id").GetString());
		Assert.Equal("LiedKarte", component.GetProperty("component").GetString());
		Assert.Equal(publishedId.ToString(), component.GetProperty("liedId").GetString());
		Assert.Equal("Die Waldfahrt", component.GetProperty("titel").GetString());
		Assert.Equal("Friedrich Silcher", component.GetProperty("komponist").GetString());
		Assert.StartsWith("Die Sänger wandern", component.GetProperty("textanfang").GetString());

		// The model never supplies and the server never sends a URL.
		var a2uiJson = JsonSerializer.Serialize(messages);
		Assert.DoesNotContain("http", a2uiJson, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("href", a2uiJson, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("url", a2uiJson, StringComparison.OrdinalIgnoreCase);

		// The citation chip stays the server-built event; the card tool call
		// and its result never reach the browser.
		Assert.Equal([publishedId.ToString()], CollectCitationIds(body));
		Assert.DoesNotContain("zeige_oberflaeche", body);
		Assert.DoesNotContain("TOOL_CALL_RESULT", body);
	}

	[Fact]
	public async Task EventsLeaveOutNullFieldsSoAStrictAgUiClientAcceptsTheStream()
	{
		// @ag-ui/client validates every event against the protocol schema and
		// refuses explicit nulls such as usage[].model; absent is what they mean.
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige mir die Waldfahrt als Karte");
		var body = await response.Content.ReadAsStringAsync();

		var events = ParseEvents(body);
		foreach (var @event in events)
		{
			// rawEvent mirrors the library's own update object; the schema accepts anything there.
			foreach (var property in @event.EnumerateObject().Where(p => p.Name != "rawEvent"))
				Assert.False(HasNull(property.Value), $"{@event.GetProperty("type")}.{property.Name} carries a null");
		}
		var finished = events.Last();
		Assert.Equal("RUN_FINISHED", finished.GetProperty("type").GetString());
		Assert.Equal("success", finished.GetProperty("outcome").GetProperty("type").GetString());
		Assert.True(finished.GetProperty("usage")[0].GetProperty("inputTokens").GetInt64() > 0);
	}

	[Fact]
	public async Task ShownCardSurvivesAReloadAndIsStoredWithTheAnswer()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();

		using var run = await RunChatAsync(client, session, threadId.ToString(), "Zeige mir die Waldfahrt als Karte");
		var streamed = CollectA2uiMessages(await run.Content.ReadAsStringAsync());

		var thread = await GetThreadAsync(client, session, threadId);
		var messages = thread.GetProperty("messages").EnumerateArray().ToList();
		Assert.Equal(2, messages.Count);
		Assert.False(messages[0].TryGetProperty("a2ui", out var onQuestion) && onQuestion.ValueKind != JsonValueKind.Null);
		var restored = messages[1].GetProperty("a2ui").EnumerateArray().ToList();
		Assert.Equal(2, restored.Count);
		Assert.Equal(
			streamed[1].GetProperty("updateComponents").GetProperty("components")[0].GetProperty("liedId").GetString(),
			restored[1].GetProperty("updateComponents").GetProperty("components")[0].GetProperty("liedId").GetString());
		Assert.Equal(publishedId.ToString(),
			restored[1].GetProperty("updateComponents").GetProperty("components")[0].GetProperty("liedId").GetString());

		using var scope = factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var stored = await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant");
		Assert.NotNull(stored.A2uiJson);
	}

	[Fact]
	public async Task ReloadDropsTheCardOfASinceDeletedLiedAndKeepsTheText()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using var run = await RunChatAsync(client, session, threadId.ToString(), "Zeige mir die Waldfahrt als Karte");
		Assert.Single(CollectA2uiMessages(await run.Content.ReadAsStringAsync()), m => m.TryGetProperty("createSurface", out _));

		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var song = await db.Lieder.SingleAsync(s => s.Id == publishedId);
			db.Lieder.Remove(song);
			await db.SaveChangesAsync();
		}

		var thread = await GetThreadAsync(client, session, threadId);
		var assistant = thread.GetProperty("messages").EnumerateArray().Last();
		Assert.Equal("assistant", assistant.GetProperty("role").GetString());
		Assert.Contains("ist im Archiv verzeichnet", assistant.GetProperty("content").GetString());
		Assert.True(!assistant.TryGetProperty("a2ui", out var a2ui) || a2ui.ValueKind == JsonValueKind.Null
			|| a2ui.GetArrayLength() == 0);
		Assert.DoesNotContain(publishedId.ToString(), assistant.GetRawText().Replace("\"content\"", ""), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TamperedStoredA2uiIsDroppedOnLoadKeepingOnlyCatalogCardsOfVisibleRecords()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, draftId, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using (await RunChatAsync(client, session, threadId.ToString(), "Die Waldfahrt")) { }

		var stored = StoredSurfaces(
			("s1", """{"id":"root","component":"LiedKarte","liedId":"__PUBLISHED__","titel":"Gefälscht"}"""),
			("s2", """{"id":"root","component":"LiedKarte","liedId":"__DRAFT__","titel":"Geheime Probe"}"""),
			("s3", """{"id":"root","component":"Button","label":"Klick","href":"https://boese.example"}"""),
			("s4", """{"id":"root","component":"LiedKarte","liedId":"__PUBLISHED__","titel":"Gefälscht","href":"https://boese.example"}"""))
			.Replace("__PUBLISHED__", publishedId.ToString()).Replace("__DRAFT__", draftId.ToString());
		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var assistant = await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant");
			assistant.A2uiJson = stored;
			await db.SaveChangesAsync();
		}

		var thread = await GetThreadAsync(client, session, threadId);
		var raw = thread.GetRawText();
		Assert.DoesNotContain("boese.example", raw);
		Assert.DoesNotContain("Geheime Probe", raw);
		Assert.DoesNotContain("Gefälscht", raw);
		var restored = thread.GetProperty("messages").EnumerateArray().Last().GetProperty("a2ui").EnumerateArray().ToList();
		Assert.Equal(2, restored.Count);
		var component = restored[1].GetProperty("updateComponents").GetProperty("components")[0];
		Assert.Equal("Die Waldfahrt", component.GetProperty("titel").GetString());
	}

	[Fact]
	public async Task AnAnswerThatWasOnlyACardIsLeftOutOnceItsCardIsGone()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, draftId, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using (await RunChatAsync(client, session, threadId.ToString(), "Die Waldfahrt")) { }
		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var assistant = await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant");
			assistant.Content = string.Empty;
			assistant.A2uiJson = StoredSurfaces(("s1", "{\"id\":\"root\",\"component\":\"LiedKarte\",\"liedId\":\"" + draftId + "\",\"titel\":\"x\"}"));
			await db.SaveChangesAsync();
		}

		var hidden = await GetThreadAsync(client, session, threadId);
		Assert.Equal(["user"], hidden.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()!).ToArray());

		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var assistant = await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant");
			assistant.A2uiJson = StoredSurfaces(("s1", "{\"id\":\"root\",\"component\":\"LiedKarte\",\"liedId\":\"" + publishedId + "\",\"titel\":\"x\"}"));
			await db.SaveChangesAsync();
		}
		var shown = await GetThreadAsync(client, session, threadId);
		var last = shown.GetProperty("messages").EnumerateArray().Last();
		Assert.Equal("assistant", last.GetProperty("role").GetString());
		Assert.Equal(string.Empty, last.GetProperty("content").GetString());
		Assert.Equal(2, last.GetProperty("a2ui").GetArrayLength());
	}

	[Fact]
	public async Task UnreadableStoredA2uiNeverBreaksTheThread()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using (await RunChatAsync(client, session, threadId.ToString(), "Die Waldfahrt")) { }
		foreach (var garbage in new[] { "{\"nicht\":\"ein array\"}", "[1,2,3]", "[{\"version\":\"v0.9\"}]", "[null]", "{}" })
		{
			await using (var scope = factory.Services.CreateAsyncScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
				var assistant = await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant");
				assistant.A2uiJson = garbage;
				await db.SaveChangesAsync();
			}

			var thread = await GetThreadAsync(client, session, threadId);
			var last = thread.GetProperty("messages").EnumerateArray().Last();
			Assert.Contains("ist im Archiv verzeichnet", last.GetProperty("content").GetString());
			Assert.True(!last.TryGetProperty("a2ui", out var a2ui) || a2ui.ValueKind == JsonValueKind.Null || a2ui.GetArrayLength() == 0,
				garbage);
		}
	}

	[Fact]
	public async Task OnlyValidVisibleRequestsOfAModelCallAreShownAndTheRestIsDropped()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, draftId, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""
			[
			 {"komponente":"LiedKarte","liedId":"{{publishedId}}"},
			 {"komponente":"LiedKarte","liedId":"{{draftId}}"},
			 {"komponente":"LiedKarte","liedId":"{{Guid.NewGuid()}}"},
			 {"komponente":"LiedKarte","liedId":"https://boese.example/lied"},
			 {"komponente":"LiedKarte","liedId":"{{publishedId}}","auftrittId":"{{Guid.NewGuid()}}"},
			 {"komponente":"Button","liedId":"{{publishedId}}"},
			 {"komponente":"basic:Text","liedId":"{{publishedId}}"},
			 {"komponente":"LiedKarte"},
			 {"komponente":"LiedKarte","liedId":"{{publishedId}}"}
			]
			""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		var messages = CollectA2uiMessages(body);
		// One valid card; the duplicate of the same record is not shown twice.
		Assert.Equal(2, messages.Count);
		Assert.Equal(publishedId.ToString(),
			messages[1].GetProperty("updateComponents").GetProperty("components")[0].GetProperty("liedId").GetString());
		Assert.DoesNotContain("Geheime Probe", body);
		Assert.DoesNotContain("boese.example", body);
		Assert.DoesNotContain(draftId.ToString(), body);

		// The model is told how many were not shown, never why or which.
		Assert.Contains("\"verworfen\":8", provider.ToolResult);
		Assert.DoesNotContain("Geheime Probe", provider.ToolResult);
		Assert.DoesNotContain(draftId.ToString(), provider.ToolResult);
	}

	[Fact]
	public async Task RequestsWithoutAnyValidComponentStreamNothingAndTheRunStillFinishes()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (_, draftId, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""[{"komponente":"LiedKarte","liedId":"{{draftId}}"},{"komponente":"Unbekannt"}]""";
		var threadId = Guid.NewGuid();

		using var response = await RunChatAsync(client, session, threadId.ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(A2uiEventName, body);
		Assert.Contains("RUN_FINISHED", body);
		Assert.DoesNotContain("RUN_ERROR", body);
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		Assert.Null((await db.ChatMessages.SingleAsync(m => m.ThreadId == threadId && m.Role == "assistant")).A2uiJson);
	}

	[Theory]
	[InlineData("liedId", "auftrittId")]
	[InlineData("liedId", "liedIds")]
	public async Task AnEntryCarryingAnIdItsComponentDoesNotTakeIsDroppedEvenForAVisibleRecord(string own, string foreign)
	{
		// No earlier valid card for this record exists, so only the catalog's
		// rule about which id a component takes can drop it.
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var foreignValue = foreign == "liedIds" ? $"[\"{publishedId}\"]" : $"\"{Guid.NewGuid()}\"";
		provider.Request = $$"""[{"komponente":"LiedKarte","{{own}}":"{{publishedId}}","{{foreign}}":{{foreignValue}}}]""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(A2uiEventName, body);
		Assert.Contains("\"verworfen\":1", provider.ToolResult);
		Assert.Contains("RUN_FINISHED", body);
	}

	[Fact]
	public async Task TheModelIsOnlyToldASurfaceAppearsWhenOneWasShown()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, draftId, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		provider.Request = $$"""[{"komponente":"LiedKarte","liedId":"{{draftId}}"}]""";
		using (await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas")) { }
		Assert.Contains("\"angezeigt\":[]", provider.ToolResult);
		Assert.Contains("Antworte nur mit Text", provider.ToolResult);
		Assert.DoesNotContain("erscheint unter deiner Antwort", provider.ToolResult);

		provider.Request = $$"""[{"komponente":"LiedKarte","liedId":"{{publishedId}}"}]""";
		using (await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas")) { }
		Assert.Contains("erscheint unter deiner Antwort", provider.ToolResult);
		Assert.DoesNotContain("Antworte nur mit Text", provider.ToolResult);
	}

	[Fact]
	public async Task UnusedFieldsLeftEmptyDoNotMakeARequestInvalid()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""
			[{"komponente":"LiedKarte","liedId":"{{publishedId}}","liedIds":[],"auftrittId":""}]
			""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");

		var component = Assert.Single(CollectA2uiMessages(await response.Content.ReadAsStringAsync()),
			m => m.TryGetProperty("updateComponents", out _)).GetProperty("updateComponents").GetProperty("components")[0];
		Assert.Equal(publishedId.ToString(), component.GetProperty("liedId").GetString());
	}

	[Fact]
	public async Task RevokedMemberCannotLoadAThreadWithCards()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using (await RunChatAsync(client, session, threadId.ToString(), "Zeige mir die Waldfahrt als Karte")) { }
		await LockOutAsync(factory, MemberA);

		using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/chat/thread/{threadId}");
		get.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(get);

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.DoesNotContain("LiedKarte", await response.Content.ReadAsStringAsync());
	}

	[Theory]
	[InlineData("\"kaputt\"")]
	[InlineData("{\"komponente\":\"LiedKarte\"}")]
	[InlineData("[{\"komponente\":\"LiedKarte\",\"liedId\":42}]")]
	[InlineData("[{\"komponente\":\"Quellen\",\"liedIds\":\"nicht-als-liste\"}]")]
	[InlineData("[\"LiedKarte\"]")]
	public async Task ArgumentsThatDoNotFitTheToolSchemaStreamNothingAndNeverFailTheRun(string request)
	{
		var provider = new ShowSurfaceChatClient { Request = request };
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(A2uiEventName, body);
		Assert.Contains("RUN_FINISHED", body);
		Assert.DoesNotContain("RUN_ERROR", body);
		Assert.Contains("Ich habe es angezeigt.", string.Concat(CollectTextDeltas(body)));
	}

	[Theory]
	[InlineData("VorschlagKarte")]
	[InlineData("AenderungsVorschau")]
	public async Task CatalogComponentsWithoutAMemberResolverAndRemovedOnesAreDroppedAndNeverOffered(string component)
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var eventId = await SeedEventAsync(factory, ownerId, "Frühlingskonzert");
		await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var argument = component == "ProgrammListe" ? $"\"auftrittId\":\"{eventId}\"" : $"\"vorschlagId\":\"{Guid.NewGuid()}\"";
		provider.Request = $$"""[{"komponente":"{{component}}",{{argument}}}]""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		Assert.DoesNotContain(A2uiEventName, body);
		Assert.Contains("\"verworfen\":1", provider.ToolResult);
		// The member chat offers the model only what it can resolve.
		Assert.Contains("LiedKarte", provider.ToolDescription);
		Assert.Contains("AuftrittKarte", provider.ToolDescription);
		Assert.Contains("Quellen", provider.ToolDescription);
		// ProgrammListe now resolves (#124); suggestions are no longer part
		// of the catalog: not even as an argument.
		Assert.Contains("ProgrammListe", provider.ToolDescription);
		Assert.DoesNotContain("orschlag", provider.ToolDescription, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("AenderungsVorschau", provider.ToolDescription);
	}

	[Fact]
	public async Task ProgrammListeShowsTheProgrammeOfASavedAuftrittInItsOrder()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (eventId, emptyEventId, hiddenLiedId) = await SeedProgrammeAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""
			[
			 {"komponente":"ProgrammListe","auftrittId":"{{eventId}}"},
			 {"komponente":"ProgrammListe","auftrittId":"{{emptyEventId}}"},
			 {"komponente":"ProgrammListe","auftrittId":"{{Guid.NewGuid()}}"}
			]
			""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige das Programm");
		var body = await response.Content.ReadAsStringAsync();
		var components = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		Assert.Single(components, c => c.GetProperty("component").GetString() == "ProgrammListe");
		var card = components.Single(c => c.GetProperty("component").GetString() == "ProgrammListe");
		Assert.Equal(eventId.ToString(), card.GetProperty("auftrittId").GetString());
		var punkte = card.GetProperty("punkte").EnumerateArray().ToList();
		Assert.Equal(2, punkte.Count);
		Assert.Equal(1, punkte[0].GetProperty("position").GetInt32());
		Assert.Equal("Eröffnung", punkte[0].GetProperty("notiz").GetString());
		Assert.False(punkte[1].TryGetProperty("notiz", out _));
		Assert.DoesNotContain(emptyEventId.ToString(), body);
		Assert.DoesNotContain(hiddenLiedId.ToString(), body);
	}

	[Fact]
	public async Task ProgrammListeHidesTrashedAuftritteAndTrashedLieder()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var eventId = Guid.NewGuid();
		var trashEventId = Guid.NewGuid();
		await using (var scope = factory.Services.CreateAsyncScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
			var now = DateTimeOffset.UtcNow;
			var liedA = new Lied
			{
				Title = "Die Waldfahrt",
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
			};
			var liedB = new Lied
			{
				Title = "Wanderers Nachtlied",
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
			};
			var trashLied = new Lied
			{
				Title = "Ausgemustertes Lied",
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
				DeletedAt = now,
				DeletedByAccountId = ownerId,
			};
			var liveEvent = new ChoirEvent
			{
				Id = eventId,
				Kind = "concert",
				Title = "Frühlingskonzert 1950",
				Venue = "Festsaal",
				DateYear = 1950,
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
			};
			var trashEvent = new ChoirEvent
			{
				Id = trashEventId,
				Kind = "concert",
				Title = "Im Papierkorb",
				DateYear = 1988,
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
				DeletedAt = now,
				DeletedByAccountId = ownerId,
			};
			var liveProgramme = new EventProgramme
			{
				EventId = liveEvent.Id,
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
			};
			liveProgramme.Items.Add(new ProgrammeItem { ProgrammeId = liveProgramme.Id, Position = 1, LiedId = liedA.Id, Note = "Eröffnung" });
			liveProgramme.Items.Add(new ProgrammeItem { ProgrammeId = liveProgramme.Id, Position = 2, LiedId = liedB.Id });
			// A trashed Lied keeps its Programmpunkt in the stored programme —
			// it only stays out of the member view.
			liveProgramme.Items.Add(new ProgrammeItem { ProgrammeId = liveProgramme.Id, Position = 3, LiedId = trashLied.Id });
			var trashProgramme = new EventProgramme
			{
				EventId = trashEvent.Id,
				CreatedAt = now,
				CreatedByAccountId = ownerId,
				UpdatedAt = now,
				UpdatedByAccountId = ownerId,
			};
			trashProgramme.Items.Add(new ProgrammeItem { ProgrammeId = trashProgramme.Id, Position = 1, LiedId = liedA.Id });
			db.Lieder.AddRange(liedA, liedB, trashLied);
			db.Events.AddRange(liveEvent, trashEvent);
			db.Programmes.AddRange(liveProgramme, trashProgramme);
			await db.SaveChangesAsync();
		}
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""
			[
			 {"komponente":"ProgrammListe","auftrittId":"{{eventId}}"},
			 {"komponente":"ProgrammListe","auftrittId":"{{trashEventId}}"}
			]
			""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige das Programm");
		var body = await response.Content.ReadAsStringAsync();
		var components = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		var card = components.Single(c => c.GetProperty("component").GetString() == "ProgrammListe");
		// The trashed Lied leaves the visible punkte untouched in its stored position.
		var punkte = card.GetProperty("punkte").EnumerateArray().ToList();
		Assert.Equal(2, punkte.Count);
		Assert.DoesNotContain("Ausgemustertes Lied", body);
		// No member sees a trashed Auftritt's programme even by its id.
		Assert.Equal(eventId.ToString(), card.GetProperty("auftrittId").GetString());
		Assert.DoesNotContain("Im Papierkorb", body);
	}

	[Fact]
	public async Task AuftrittKarteShowsSavedEventsOnlyAndQuellenOnlyKnownLieder()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var published = await SeedEventAsync(factory, ownerId, "Frühlingskonzert 1932");
		// Never saved: an id no record stands behind resolves to nothing.
		var draft = Guid.NewGuid();
		var (liedId, unknownLied, _) = await SeedLiederAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = $$"""
			[
			 {"komponente":"AuftrittKarte","auftrittId":"{{published}}"},
			 {"komponente":"AuftrittKarte","auftrittId":"{{draft}}"},
			 {"komponente":"Quellen","liedIds":["{{liedId}}","{{unknownLied}}","kein-guid"]},
			 {"komponente":"Quellen","liedIds":["{{unknownLied}}"]}
			]
			""";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");
		var body = await response.Content.ReadAsStringAsync();

		var components = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		Assert.Equal(2, components.Count);
		Assert.Equal("AuftrittKarte", components[0].GetProperty("component").GetString());
		Assert.Equal(published.ToString(), components[0].GetProperty("auftrittId").GetString());
		Assert.Equal("Frühlingskonzert 1932", components[0].GetProperty("titel").GetString());
		Assert.Equal("concert", components[0].GetProperty("art").GetString());
		Assert.Equal("Quellen", components[1].GetProperty("component").GetString());
		var sources = components[1].GetProperty("quellen").EnumerateArray().ToList();
		Assert.Equal(liedId.ToString(), Assert.Single(sources).GetProperty("liedId").GetString());
		Assert.DoesNotContain("Geheime Probe", body);
	}

	[Fact]
	public async Task AtMostFourCardsAreShownPerAnswer()
	{
		var provider = new ShowSurfaceChatClient();
		await using var factory = ChatFactory(chatClient: provider);
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (publishedId, _, instructionId) = await SeedLiederAsync(factory, ownerId);
		var events = new List<Guid>();
		for (var i = 0; i < 4; i++)
			events.Add(await SeedEventAsync(factory, ownerId, $"Konzert {i}"));
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		provider.Request = "[" + string.Join(",", events.Select(id => $$"""{"komponente":"AuftrittKarte","auftrittId":"{{id}}"}""")
			.Append($$"""{"komponente":"LiedKarte","liedId":"{{publishedId}}"}""")
			.Append($$"""{"komponente":"LiedKarte","liedId":"{{instructionId}}"}""")) + "]";

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(), "Zeige etwas");

		var surfaces = CollectA2uiMessages(await response.Content.ReadAsStringAsync())
			.Count(m => m.TryGetProperty("createSurface", out _));
		Assert.Equal(4, surfaces);
		Assert.Contains("\"verworfen\":2", provider.ToolResult);
	}

	[Fact]
	public async Task UnauthenticatedAndForeignThreadsStillNeverReturnCards()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedMemberAsync(factory, MemberB);
		await SeedLiederAsync(factory, ownerId);
		var sessionA = await SignInAsync(factory, MemberA);
		var sessionB = await SignInAsync(factory, MemberB);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid();
		using (await RunChatAsync(client, sessionA, threadId.ToString(), "Zeige mir die Waldfahrt als Karte")) { }

		using var foreign = new HttpRequestMessage(HttpMethod.Get, $"/api/chat/thread/{threadId}");
		foreign.Headers.Add("Cookie", sessionB);
		using var foreignResponse = await client.SendAsync(foreign);
		using var anonymousRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/chat/thread/{threadId}");
		using var anonymous = await client.SendAsync(anonymousRequest);

		Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
		Assert.DoesNotContain("LiedKarte", await foreignResponse.Content.ReadAsStringAsync());
	}

	// ---- helpers for the card tests ----

	private static async Task<JsonElement> GetThreadAsync(HttpClient client, string session, Guid threadId)
	{
		using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/chat/thread/{threadId}");
		get.Headers.Add("Cookie", session);
		using var response = await client.SendAsync(get);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		return await response.Content.ReadFromJsonAsync<JsonElement>();
	}

	private static bool HasNull(JsonElement element) => element.ValueKind switch
	{
		JsonValueKind.Null => true,
		JsonValueKind.Object => element.EnumerateObject().Any(p => HasNull(p.Value)),
		JsonValueKind.Array => element.EnumerateArray().Any(HasNull),
		_ => false,
	};

	private static List<JsonElement> CollectA2uiMessages(string sseBody) => ParseEvents(sseBody)
		.Where(e => e.GetProperty("type").GetString() == "CUSTOM" && e.GetProperty("name").GetString() == A2uiEventName)
		.SelectMany(e => e.GetProperty("value").EnumerateArray())
		.ToList();

	/// <summary>Builds a stored message: one createSurface and one updateComponents per surface.</summary>
	private static string StoredSurfaces(params (string SurfaceId, string ComponentJson)[] surfaces)
		=> "[" + string.Join(",", surfaces.SelectMany(s => new[]
		{
			"{\"version\":\"v0.9\",\"createSurface\":{\"surfaceId\":\"" + s.SurfaceId + "\",\"catalogId\":\"" + CatalogId + "\"}}",
			"{\"version\":\"v0.9\",\"updateComponents\":{\"surfaceId\":\"" + s.SurfaceId + "\",\"components\":[" + s.ComponentJson + "]}}",
		})) + "]";

	/// <summary>An Auftritt with a two-entry Programm, one with no Programm and an id saved nowhere.</summary>
	private static async Task<(Guid WithProgramme, Guid WithoutProgramme, Guid UnsavedLiedId)> SeedProgrammeAsync(
		AuthApiFactory factory, Guid ownerId)
	{
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var liedA = new Lied
		{
			Title = "Die Waldfahrt",
			Composer = "Friedrich Silcher",
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		var liedB = new Lied
		{
			Title = "Wanderers Nachtlied",
			Composer = "Franz Schubert",
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		var withProgramme = new ChoirEvent
		{
			Kind = "concert",
			Title = "Frühlingskonzert 1950",
			Venue = "Festsaal",
			DateYear = 1950,
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		var withoutProgramme = new ChoirEvent
		{
			Kind = "concert",
			Title = "Kammerabend",
			DateYear = 1999,
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		var programme = new EventProgramme
		{
			EventId = withProgramme.Id,
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		programme.Items.Add(new ProgrammeItem { ProgrammeId = programme.Id, Position = 1, LiedId = liedA.Id, Note = "Eröffnung" });
		programme.Items.Add(new ProgrammeItem { ProgrammeId = programme.Id, Position = 2, LiedId = liedB.Id });
		db.Lieder.AddRange(liedA, liedB);
		db.Events.AddRange(withProgramme, withoutProgramme);
		db.Programmes.Add(programme);
		await db.SaveChangesAsync();
		return (withProgramme.Id, withoutProgramme.Id, Guid.NewGuid());
	}

	private static async Task<Guid> SeedEventAsync(AuthApiFactory factory, Guid ownerId, string title)
	{
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var entity = new ChoirEvent
		{
			Kind = "concert",
			Title = title,
			Venue = "Festsaal",
			DateYear = 1932,
			DateMonth = 5,
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		db.Events.Add(entity);
		await db.SaveChangesAsync();
		return entity.Id;
	}
}

/// <summary>
/// Provider stand-in whose first call asks for <c>zeige_oberflaeche</c> with
/// the configured <c>komponenten</c> JSON, and whose second call (after the
/// tool result) answers in text. It records what the model was told.
/// </summary>
internal sealed class ShowSurfaceChatClient : IChatClient
{
	/// <summary>The JSON array the "model" passes as <c>komponenten</c>.</summary>
	public string Request { get; set; } = "[]";

	/// <summary>The result text the tool handed back to the model.</summary>
	public string ToolResult { get; private set; } = string.Empty;

	/// <summary>The description of the tool as offered to the model.</summary>
	public string ToolDescription { get; private set; } = string.Empty;

	public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
		CancellationToken cancellationToken = default) => throw new NotSupportedException();

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<AiChatMessage> messages, AiChatOptions? options = null,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await Task.Yield();
		var tool = options?.Tools?.SingleOrDefault(t => t.Name == "zeige_oberflaeche");
		ToolDescription = tool is null ? string.Empty : tool.Description + JsonSerializer.Serialize((tool as AIFunction)?.JsonSchema);
		var conversation = messages.ToList();
		if (conversation.Last().Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
		{
			ToolResult = result.Result?.ToString() ?? string.Empty;
			yield return new ChatResponseUpdate(ChatRole.Assistant, "Ich habe es angezeigt.");
			yield return Usage();
			yield break;
		}
		yield return new ChatResponseUpdate
		{
			Contents = [new FunctionCallContent("zeige_1", "zeige_oberflaeche",
				new Dictionary<string, object?> { ["komponenten"] = JsonDocument.Parse(Request).RootElement.Clone() })],
		};
		yield return Usage();
	}

	private static ChatResponseUpdate Usage() => new()
	{
		Contents = [new UsageContent(new UsageDetails { InputTokenCount = 50, OutputTokenCount = 10 })],
	};

	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
	}
}
