using System.Reflection;
using System.Text.RegularExpressions;

using Xunit;

namespace InternetData.Tests;

/// <summary>
/// Every request names this library, <c>internetdata-dotnet/&lt;version&gt;</c>: a lookup, the
/// entitlement, a database call, OAuth's discovery and object storage's transfer. Read where the
/// caller's own handler sits, so it is what a wrapping program sees and may put its name before.
/// </summary>
public class UserAgentTests
{
    [Fact]
    public async Task EveryRequestNamesTheLibraryAndItsVersion()
    {
        var seen = new List<(string Path, string? Agent)>();
        var handler = new StubHandler(request =>
        {
            lock (seen)
            {
                seen.Add((request.RequestUri!.AbsolutePath,
                    request.Headers.TryGetValues("User-Agent", out var agent) ? string.Join(" ", agent) : null));
            }
            if (request.RequestUri!.Host == "storage.test")
            {
                return StubHandler.Json(new Route("data", 200));
            }
            if (request.RequestUri.AbsolutePath.Contains("/database/download", StringComparison.Ordinal))
            {
                return StubHandler.Json(new Route(
                    "", 302, new Dictionary<string, string> { ["Location"] = "https://storage.test/blob" }));
            }
            if (request.RequestUri.AbsolutePath == "/8.8.8.8")
            {
                return StubHandler.Json(new Route(Stub.LookupBody("8.8.8.8")));
            }
            return StubHandler.Json(new Route("""{"error":"no"}""", 500));
        });
        using var client = Stub.Client(handler, new InternetDataClientOptions { Retries = 0 });

        await client.LookupAsync("8.8.8.8");
        await Record.ExceptionAsync(() => client.MyEntitlementAsync());
        await Record.ExceptionAsync(() => client.Database.ListAsync());
        await Record.ExceptionAsync(() => client.Oauth.MetadataAsync());
        await client.Database.DownloadBytesAsync("cdn_ip_v1", DatabaseFormat.Mmdb);

        var version = typeof(InternetDataClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Assert.Matches(new Regex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z"), version);
        Assert.Equal(6, seen.Count);
        Assert.Contains(seen, s => s.Path == "/blob");
        Assert.Contains(seen, s => s.Path == "/.well-known/oauth-authorization-server");
        Assert.All(seen, s => Assert.Equal("internetdata-dotnet/" + version, s.Agent));
    }
}
