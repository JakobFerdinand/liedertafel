using System.Net;
using Archive.Backend.Chat;
using Archive.Backend.Data;
using Archive.Backend.Events;
using Archive.Backend.Lieder;
using Archive.Backend.Recordings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Backend.Tests;

/// <summary>
/// #124: the chat answers "Wann haben wir dieses Lied gesungen?" for a Lied
/// from what the audience may see: the Programm (the record of what was sung)
/// plus Lied-Aufnahmen naming an Auftritt, honest about approximate dates,
/// naming Zeitmarken where they exist, and shows the Lied and Auftritt cards
/// (scripted client, #124).
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
		// The history answer shows the Lied and the sung Auftritt as cards.
		var cards = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		Assert.Equal(["LiedKarte", "AuftrittKarte"], cards.Select(c => c.GetProperty("component").GetString()).ToList());
		Assert.Equal(waldfahrt.ToString(), cards[0].GetProperty("liedId").GetString());
		Assert.Equal("Sängerfest", cards[1].GetProperty("titel").GetString());
	}

	[Fact]
	public async Task HistoryAnswerStaysHonestAboutApproximateDatesAndNamesTheZeitmarke()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedZeitmarkeHistoryAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(),
			"Wann haben wir das Lied Die Waldfahrt gesungen?");
		var body = await response.Content.ReadAsStringAsync();
		var text = string.Concat(CollectTextDeltas(body));
		// The approximate year stays as the record keeps it; nothing invents a day.
		Assert.Contains("um 1950", text);
		Assert.DoesNotContain("Mai", text);
		// The published Aufnahme's Zeitmarke is named; the unpublished recording
		// (and any pretended start time) never appears.
		Assert.Contains("Zeitmarke", text);
		Assert.Contains("Konzertmitschnitt", text);
		Assert.Contains("Aufnahme des Liedes", text);
		Assert.DoesNotContain("Geheimmitschnitt", body);
		var cards = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		Assert.Contains(cards, c => c.GetProperty("component").GetString() == "AuftrittKarte"
			&& c.GetProperty("datum").GetString() == "um 1950");
	}

	[Fact]
	public async Task HistoryWithOnlyAProgrammeShowsTheProgrammListeAndSeparatesPlannedFromSung()
	{
		await using var factory = ChatFactory();
		var ownerId = await SeedMemberAsync(factory, MemberA);
		await SeedPlannedHistoryAsync(factory, ownerId);
		var session = await SignInAsync(factory, MemberA);
		using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

		using var response = await RunChatAsync(client, session, Guid.NewGuid().ToString(),
			"Wann haben wir das Lied Die Waldfahrt gesungen?");
		var body = await response.Content.ReadAsStringAsync();
		var text = string.Concat(CollectTextDeltas(body));
		Assert.Contains("steht im Programm", text);
		Assert.Contains("Mai-Konzert", text);
		Assert.DoesNotContain("keine Auftritte verzeichnet", text);
		var cards = CollectA2uiMessages(body)
			.Where(m => m.TryGetProperty("updateComponents", out _))
			.Select(m => m.GetProperty("updateComponents").GetProperty("components")[0])
			.ToList();
		Assert.Equal(["LiedKarte", "ProgrammListe"], cards.Select(c => c.GetProperty("component").GetString()).ToList());
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

	private static ChoirEvent NewEvent(string title, Guid ownerId, DateTimeOffset now,
		int year = 1932, int month = 5, int day = 14, bool approximate = false) => new()
	{
		Kind = "concert",
		Title = title,
		DateYear = year,
		DateMonth = month == 0 ? null : month,
		DateDay = month == 0 || day == 0 ? null : day,
		DateApproximate = approximate,
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

	/// <summary>
	/// One sung Auftritt with an approximate year ("um 1950"), a published
	/// Aufnahme whose Zeitmarke marks its entry, a Lied-Aufnahme naming it and
	/// an unpublished recording that must never surface.
	/// </summary>
	private static async Task<Guid> SeedZeitmarkeHistoryAsync(AuthApiFactory factory, Guid ownerId)
	{
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var waldfahrt = NewLied("Die Waldfahrt", "Friedrich Silcher", ownerId, now);
		var fest = NewEvent("Jubiläumsfest", ownerId, now, year: 1950, month: 0, day: 0, approximate: true);
		db.Lieder.Add(waldfahrt);
		db.Events.Add(fest);
		db.Programmes.Add(NewProgramme(fest, ownerId, now, waldfahrt));
		db.Dateien.Add(new Datei
		{
			LiedId = waldfahrt.Id,
			Kind = DateiKinds.Aufnahme,
			RecordedAtEventId = fest.Id,
			OriginalFileName = "choraufnahme.m4a",
			BlobName = $"dateien/{Guid.NewGuid():N}",
			ContentType = "audio/mp4",
			SizeBytes = 10,
			CreatedByAccountId = ownerId,
			CreatedAt = now,
			RowVersion = 1,
		});
		var visible = new Recording { EventId = fest.Id, Label = "Konzertmitschnitt", Kind = RecordingKinds.Audio, PublishedAt = now, CreatedAt = now, UpdatedAt = now };
		var hidden = new Recording { EventId = fest.Id, Label = "Geheimmitschnitt", Kind = RecordingKinds.Audio, CreatedAt = now, UpdatedAt = now };
		db.Recordings.AddRange(visible, hidden);
		await db.SaveChangesAsync();
		var markedItem = db.Programmes.Local.Single(p => p.EventId == fest.Id).Items.Single();
		db.RecordingPassages.AddRange(
			new RecordingPassage { RecordingId = visible.Id, ProgrammeItemId = markedItem.Id, EventId = fest.Id, StartSeconds = 5, EndSeconds = 60, CreatedAt = now, UpdatedAt = now },
			new RecordingPassage { RecordingId = hidden.Id, ProgrammeItemId = markedItem.Id, EventId = fest.Id, StartSeconds = 5, EndSeconds = 60, CreatedAt = now, UpdatedAt = now });
		await db.SaveChangesAsync();
		return waldfahrt.Id;
	}

	/// <summary>One upcoming Auftritt whose Programm names the Lied — geplant, nothing sung.</summary>
	private static async Task<Guid> SeedPlannedHistoryAsync(AuthApiFactory factory, Guid ownerId)
	{
		await using var scope = factory.Services.CreateAsyncScope();
		var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
		var now = DateTimeOffset.UtcNow;
		var waldfahrt = NewLied("Die Waldfahrt", "Friedrich Silcher", ownerId, now);
		var konzert = NewEvent("Mai-Konzert", ownerId, now, year: 2098);
		db.Lieder.Add(waldfahrt);
		db.Events.Add(konzert);
		db.Programmes.Add(NewProgramme(konzert, ownerId, now, waldfahrt));
		await db.SaveChangesAsync();
		return waldfahrt.Id;
	}
}
