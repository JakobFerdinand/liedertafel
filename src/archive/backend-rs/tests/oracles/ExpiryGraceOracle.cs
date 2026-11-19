// Compile outside the worktree with Microsoft.AspNetCore.App and a reference
// to the freshly built, unchanged Archive.Backend.dll. No host/services run.
using System.Globalization;
using System.Text.Json;
using Archive.Backend.Assets;
using Microsoft.Extensions.Configuration;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-AT");
string?[] inputs = [
    null, "", " ", "0", "-0", "+0", "1", "-1", "+1", "--1", "0000000000001",
    "  -0:02 \t", "\u0085\u00a0-0:02\u3000", "\u001c0", "\u200b0", "\ufeff0",
    "- 0:02", " 00: 00:00", "０", "١", "0x1", "1_0", "0.1", "1:2", "1.2:3",
    "1:2:3", "1.2:3:4", "1:2:3:4", "1.2:3:.5", "1:2:3:.5", "1:02:.3",
    "1:02.3", "1:02:03,4", "1:02:03.", "1:02::3", "1:02:03:04:05", "1:02:03..4",
    "00023:00059:00059", "23:59", "24:00", "24:00:00", "25:01:00", "25:25:59",
    "23:60", "0:59:60", "1.24:00:00", "1:24:00:00", "1:00:60:00", "1:00:00:60",
    "0:00:00.1", "-0:00:00.1", "0:00:00.9999999", "0:00:00.99999999",
    "0:00:00.12345678", "0:00:00.10000000", "0:00:00.012345678",
    "0:00:00.0123456789", "1:12:59.0012345678", "-1:12:59.0012345678",
    "0:00:00.09999999", "0:00:00.0999999999", "0:00:00.0268435455", "0:00:00.0268435456",
    "0:00:00.00000004", "0:00:00.00000005", "0:00:00.00000015", "-0:00:00.00000015",
    "0:00:00.00000010", "0:00:00.00000000000000000000000015", "0:00:00.00000000000000000",
    "0:00:00.000001268435455", "0:00:00.0000001268435455", "0:00:00.00000001268435455",
    "0:00:00.000000001268435455", "0:00:00.000000001268435456",
    "0:00:00.00000019999999", "0:00:00.000000126843545", "0:00:00.0000000268435455",
    "0:00:00.0000000123456789", "0:00:00.00000000123456789",
    "10675199", "10675200", "10675199.02:48:05.4775807", "10675199.02:48:05.4775808",
    "-10675199.02:48:05.4775807", "-10675199.02:48:05.4775808", "-10675199.02:48:05.4775809",
    "10675199:02:48:05.4775807", "10675199.02:48:06", "-10675199.02:48:06",
    "268435455", "268435456", "9999999999999999999999999",
];
const string key = "Archive:Assets:ExpiryGrace";
var rows = inputs.Select(input => {
    var values = input is null ? [] : new Dictionary<string, string?> { [key] = input };
    var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    var options = new AssetStorageOptions();
    try {
        config.GetSection("Archive:Assets").Bind(options);
        return new { input, ticks = (long?)options.ExpiryGrace.Ticks, error = (string?)null };
    } catch (InvalidOperationException e) {
        return new { input, ticks = (long?)null, error = (string?)(e.InnerException?.GetType().Name ?? e.GetType().Name) };
    }
});
Console.WriteLine(JsonSerializer.Serialize(new {
    runtime = Environment.Version.ToString(),
    source = "ConfigurationBuilder.Bind + unchanged AssetStorageOptions (current culture de-AT)",
    rows,
}));
