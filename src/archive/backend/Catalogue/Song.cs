namespace Archive.Backend.Catalogue;

/// <summary>
/// A choir song (ARC-013). Published state is explicit: <see cref="PublishedAt"/>
/// null means draft. Attribution and the application-bumped optimistic
/// concurrency token follow the auth entities (ARC-007 pattern).
/// </summary>
public sealed class Song
{
	public Guid Id { get; set; } = Guid.CreateVersion7();

	public required string Title { get; set; }

	public string? Composer { get; set; }

	public string? Lyricist { get; set; }

	/// <summary>Publication stamp; null while the song is a draft.</summary>
	public DateTimeOffset? PublishedAt { get; set; }

	public Guid? PublishedByAccountId { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	public Guid CreatedByAccountId { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Guid UpdatedByAccountId { get; set; }

	/// <summary>
	/// Application-bumped optimistic-concurrency token, mirroring
	/// <see cref="Auth.SignInChallenge.RowVersion"/>: every mutation increments
	/// it alongside the change.
	/// </summary>
	public uint RowVersion { get; set; }

	/// <summary>Optional entered lyrics or opening words (ARC-020).</summary>
	public string? Lyrics { get; set; }

	public List<Arrangement> Arrangements { get; set; } = [];

	public List<SongTitle> AlternateTitles { get; set; } = [];
}
