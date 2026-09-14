using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace Archive.Backend.Auth;

/// <summary>
/// Pure helpers: normalization, code generation, salted hashing and IP hashing.
/// No logging here; callers must never log codes, emails or raw IPs.
/// </summary>
public static class AuthSecurity
{
	public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

	public static bool TryNormalizeEmail(string? email, out string normalized)
	{
		normalized = string.Empty;
		if (string.IsNullOrWhiteSpace(email) || email.Length > 320)
			return false;
		var trimmed = email.Trim();
		try
		{
			_ = new MailAddress(trimmed);
		}
		catch (FormatException)
		{
			return false;
		}
		if (!trimmed.Contains('@'))
			return false;
		normalized = trimmed.ToUpperInvariant();
		return true;
	}

	public static string NormalizeCode(string code)
	{
		var builder = new StringBuilder(code.Length);
		foreach (var ch in code)
		{
			if (char.IsDigit(ch))
				builder.Append(ch);
		}
		return builder.ToString();
	}

	public static string GenerateCode(int length = 6)
	{
		var max = (int)Math.Pow(10, length);
		return RandomNumberGenerator.GetInt32(0, max).ToString($"D{length}");
	}

	public static byte[] NewSalt(int size = 16)
	{
		var salt = new byte[size];
		RandomNumberGenerator.Fill(salt);
		return salt;
	}

	public static byte[] HashCode(string code, byte[] salt)
	{
		var codeBytes = Encoding.UTF8.GetBytes(code);
		var combined = new byte[salt.Length + codeBytes.Length];
		Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
		Buffer.BlockCopy(codeBytes, 0, combined, salt.Length, codeBytes.Length);
		return SHA256.HashData(combined);
	}

	public static bool VerifyCode(string candidate, byte[] salt, byte[] expectedHash)
	{
		var actual = HashCode(candidate, salt);
		return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
	}

	/// <summary>SHA-256 hex of the client IP. Stored for abuse limits; the raw IP is never stored or logged.</summary>
	public static string HashIp(string? ip)
	{
		var bytes = Encoding.UTF8.GetBytes(ip ?? "unknown");
		return Convert.ToHexString(SHA256.HashData(bytes));
	}

	public static string DomainOf(string normalizedEmail)
	{
		var at = normalizedEmail.LastIndexOf('@');
		return at >= 0 ? normalizedEmail[(at + 1)..] : "unknown";
	}
}
