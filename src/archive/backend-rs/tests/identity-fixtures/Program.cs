// Synthetic compatibility fixtures ONLY; never reads configuration/member data.
using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.Extensions.Options;

const string password = "Synthetic-only-Pässword!";
if (args.Contains("--normalization"))
{
    Console.WriteLine("straße@liedertafel.test".Trim().ToUpperInvariant());
    return;
}
var vectors = new List<object>();
foreach (var mode in new[] { PasswordHasherCompatibilityMode.IdentityV2, PasswordHasherCompatibilityMode.IdentityV3 })
{
    var hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { CompatibilityMode = mode }));
    var hash = hasher.HashPassword(new object(), password);
    vectors.Add(new { mode = mode.ToString(), password, hash });
}
foreach (var prf in new[] { KeyDerivationPrf.HMACSHA1, KeyDerivationPrf.HMACSHA256 })
{
    var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    var key = KeyDerivation.Pbkdf2(password, salt, prf, 10000, 32);
    var bytes = new byte[13 + salt.Length + key.Length];
    bytes[0] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), (uint)prf);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), 10000);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), (uint)salt.Length);
    salt.CopyTo(bytes, 13); key.CopyTo(bytes, 29);
    vectors.Add(new { mode = "IdentityV3-" + prf, password, hash = Convert.ToBase64String(bytes) });
}
Console.WriteLine(JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }));
