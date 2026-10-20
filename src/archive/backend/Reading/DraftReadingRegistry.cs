using System.Collections.Concurrent;

namespace Archive.Backend.Reading;

/// <summary>
/// The model calls in flight for persisted draft Dateien (#178), keyed by draft
/// and Datei. Such a reading is deliberately independent of the HTTP request,
/// so it finishes when the tab was closed. What the editor does on purpose
/// (discard the draft, remove the Datei, retry, a newer request) must stop the
/// spending instead: those callers cancel here. In memory per instance; a
/// reading on another instance runs to its time limit and then changes nothing.
/// </summary>
public sealed class DraftReadingRegistry
{
	private readonly ConcurrentDictionary<(Guid Entwurf, Guid Datei), CancellationTokenSource> running = new();

	/// <summary>Starts tracking a reading; an earlier one for the same Datei is cancelled.</summary>
	public Handle Start(Guid entwurfId, Guid dateiId, TimeSpan limit)
	{
		var source = new CancellationTokenSource(limit);
		running.AddOrUpdate((entwurfId, dateiId), source, (_, earlier) =>
		{
			Cancel(earlier);
			return source;
		});
		return new Handle(this, (entwurfId, dateiId), source);
	}

	public void CancelDatei(Guid entwurfId, Guid dateiId)
	{
		if (running.TryRemove((entwurfId, dateiId), out var source))
			Cancel(source);
	}

	public void CancelEntwurf(Guid entwurfId)
	{
		foreach (var key in running.Keys.Where(k => k.Entwurf == entwurfId))
			CancelDatei(key.Entwurf, key.Datei);
	}

	private static void Cancel(CancellationTokenSource source)
	{
		try { source.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	public sealed class Handle : IDisposable
	{
		private readonly DraftReadingRegistry owner;
		private readonly (Guid Entwurf, Guid Datei) key;
		private readonly CancellationTokenSource source;

		internal Handle(DraftReadingRegistry owner, (Guid Entwurf, Guid Datei) key, CancellationTokenSource source)
		{
			this.owner = owner;
			this.key = key;
			this.source = source;
		}

		public CancellationToken Token => source.Token;

		public void Dispose()
		{
			// Only remove the entry if it is still this reading's.
			owner.running.TryRemove(new KeyValuePair<(Guid Entwurf, Guid Datei), CancellationTokenSource>(key, source));
			source.Dispose();
		}
	}
}
