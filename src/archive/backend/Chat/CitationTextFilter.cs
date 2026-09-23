using System.Text;

namespace Archive.Backend.Chat;

/// <summary>
/// Validates inline source markers before they leave the server, including
/// markers split across provider chunks. Ordinary answer text remains streamed.
/// </summary>
internal sealed class CitationTextFilter(Func<string, bool> isAuthorizedTitle)
{
	private const string Prefix = "[Quelle:";
	private string pending = string.Empty;

	public string Append(string text)
	{
		pending += text;
		var output = new StringBuilder();
		while (pending.Length > 0)
		{
			var start = pending.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
			if (start < 0)
			{
				// A suffix may be the beginning of the next chunk's marker.
				var held = Math.Min(Prefix.Length - 1, pending.Length);
				while (held > 0 && !Prefix.StartsWith(pending[^held..], StringComparison.OrdinalIgnoreCase))
					held--;
				output.Append(pending[..(pending.Length - held)]);
				pending = held == 0 ? string.Empty : pending[^held..];
				break;
			}
			output.Append(pending[..start]);
			pending = pending[start..];
			// A song title may itself contain brackets, e.g. Abendlied [SATB].
			// Only the closing bracket of the whole marker terminates the title.
			var depth = 1;
			var end = -1;
			for (var i = Prefix.Length; i < pending.Length; i++)
			{
				if (pending[i] == '[')
					depth++;
				else if (pending[i] == ']' && --depth == 0)
				{
					end = i;
					break;
				}
			}
			if (end < 0)
				break;
			var title = pending[Prefix.Length..end].Trim();
			if (isAuthorizedTitle(title))
				output.Append("[Quelle: ").Append(title).Append(']');
			pending = pending[(end + 1)..];
		}
		return output.ToString();
	}

	public string Complete()
	{
		// Drop an unterminated source marker; a lone '[' is ordinary text.
		var text = pending.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? string.Empty : pending;
		pending = string.Empty;
		return text;
	}
}
