using System.Net;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #170: the chat answers "Wann haben wir dieses Lied gesungen?" for a Lied
/// from what the audience may see: Lieder in the Programm of published
/// Auftritte, never drafts, and shows the Lied's card (scripted client).
/// </summary>
public sealed partial class ChatApiTests
{
	[Fact]
	public async Task ChatAnswersWhenALiedWasSungFromPublishedAuftritteAndShowsItsCard()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		var (waldfahrt, _) = await SeedLiedHistoryAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
		var threadId = Guid.NewGuid().ToString();
		using (await RunChatAsync(client, session, threadId, "Zeige mir die Waldfahrt als Karte")) { }

		using var response = await RunChatAsync(client, session, threadId, "Wann haben wir dieses Lied gesungen?");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var body = await response.Content.ReadAsStringAsync();
		var text = string.Concat(CollectTextDeltas(body));
		Assert.Contains("Sängerfest", text);
		Assert.Contains("1932", text);
		Assert.DoesNotContain(ScriptedChatClient.NoPerformanceDataAnswer, text);
		Assert.Equal([waldfahrt.ToString()], CollectCitationIds(body));
		var card = CollectA2uiMessages(body)
			.Single(m => m.TryGetProperty("updateComponents", out _))
			.GetProperty("updateComponents").GetProperty("components")[0];
		Assert.Equal("LiedKarte", card.GetProperty("component").GetString());
		Assert.Equal(waldfahrt.ToString(), card.GetProperty("liedId").GetString());
	}

	[Fact]
	public async Task ChatNamesAUnsungLiedHonestlyAndAnswersByTitleWithoutEarlierContext()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedLiedHistoryAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var unsung = await RunChatAsync(client, session, Guid.NewGuid().ToString(),
			"Wann haben wir das Lied Abendlied gesungen?");
		var unsungText = string.Concat(CollectTextDeltas(await unsung.Content.ReadAsStringAsync()));
		Assert.Contains("Abendlied", unsungText);
		Assert.Contains("keine", unsungText, StringComparison.OrdinalIgnoreCase);

		using var sung = await RunChatAsync(client, session, Guid.NewGuid().ToString(),
			"Wann haben wir das Lied Die Waldfahrt gesungen?");
		Assert.Contains("Sängerfest", string.Concat(CollectTextDeltas(await sung.Content.ReadAsStringAsync())));
	}

	private static async Task<(Guid Waldfahrt, Guid Abendlied)> SeedLiedHistoryAsync(AuthApiFactory factory, Guid ownerId)
	{
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var waldfahrt = NewLied("Die Waldfahrt", "Friedrich Silcher", ownerId, now);
		var abendlied = NewLied("Abendlied", "Franz Schubert", ownerId, now);
		var sangerfest = NewEvent("Sängerfest", ownerId, now);
		db.Lieder.AddRange(waldfahrt, abendlied);
		db.Events.Add(sangerfest);
		db.Programmes.Add(NewProgramme(sangerfest, ownerId, now, waldfahrt));
		await db.SaveChangesAsync();
		return (waldfahrt.Id, abendlied.Id);
	}

	private static Lied NewLied(string title, string composer, Guid ownerId, DateTimeOffset now) => new()
	{
		Title = title,
		Composer = composer,
		CreatedAt = now,
		CreatedByAccountId = ownerId,
		UpdatedAt = now,
		UpdatedByAccountId = ownerId,
	};

	private static ChoirEvent NewEvent(string title, Guid ownerId, DateTimeOffset now) => new()
	{
		Kind = "concert",
		Title = title,
		DateYear = 1932,
		DateMonth = 5,
		DateDay = 14,
		CreatedAt = now,
		CreatedByAccountId = ownerId,
		UpdatedAt = now,
		UpdatedByAccountId = ownerId,
	};

	private static EventProgramme NewProgramme(ChoirEvent choirEvent, Guid ownerId, DateTimeOffset now, params Lied[] lieder)
	{
		var programme = new EventProgramme
		{
			EventId = choirEvent.Id,
			CreatedAt = now,
			CreatedByAccountId = ownerId,
			UpdatedAt = now,
			UpdatedByAccountId = ownerId,
		};
		for (var i = 0; i < lieder.Length; i++)
			programme.Items.Add(new ProgrammeItem { ProgrammeId = programme.Id, Position = i + 1, LiedId = lieder[i].Id });
		return programme;
	}
}
