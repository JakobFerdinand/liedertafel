using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Archive.Backend.Tests;

/// <summary>
/// Creates Lieder over HTTP for the suites that pass an editor session cookie
/// around (programme, history, recordings, chat).
/// </summary>
internal static class LiedFixtures
{
	/// <summary>Creates a Lied as the signed-in editor and returns its id.</summary>
	public static async Task<Guid> CreateLiedAsync(
		HttpClient client, string editorSession, string title,
		string? voiceConfiguration = null, string? musicalKey = null)
	{
		using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/antiforgery");
		tokenRequest.Headers.Add("Cookie", editorSession);
		using var tokenResponse = await client.SendAsync(tokenRequest);
		tokenResponse.EnsureSuccessStatusCode();
		var cookie = Assert.Single(tokenResponse.Headers.GetValues("Set-Cookie")).Split(';')[0];
		var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
		using var create = new HttpRequestMessage(HttpMethod.Post, "/api/lieder");
		create.Headers.Add("Cookie", $"{cookie}; {editorSession}");
		create.Headers.Add("X-CSRF-TOKEN", token);
		create.Content = JsonContent.Create(new { title, voiceConfiguration, musicalKey });
		using var response = await client.SendAsync(create);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		var body = await response.Content.ReadFromJsonAsync<JsonElement>();
		return Guid.Parse(body.GetProperty("lied").GetProperty("id").GetString()!);
	}
}
