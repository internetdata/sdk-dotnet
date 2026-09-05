using System.Reflection;
using System.Text.Json;

using Xunit;

namespace InternetData.Integration;

// This suite is worthless if the toolchain handed it the working tree, and that failure is
// SILENT: every test passes, against the wrong code. The assembly's own location cannot answer it,
// because a PackageReference and a ProjectReference both end up copied into the output directory.
// The deps.json beside it can: it records where each dependency came from.
public class ProvenanceTests
{
    [Fact]
    public void TheSuiteRunsAgainstAPublishedPackageNotLocalSource()
    {
        var library = Resolved();

        Assert.True(
            library.Type == "package",
            $"InternetData was resolved as a \"{library.Type}\", so these tests would run against "
            + "something other than the release. Only a PackageReference proves anything here.");
        // The same fact read a second way: a project reference carries no digest. It does NOT
        // distinguish nuget.org from a folder feed, which writes one too; that is what the version
        // check below and the runner's registry query are for.
        Assert.False(
            string.IsNullOrEmpty(library.Sha512),
            "the resolved dependency carries no digest, so it was not restored as a package");
    }

    [Fact]
    public void TheResolvedVersionIsTheOneTheRunnerAskedFor()
    {
        var expected = Environment.GetEnvironmentVariable("INTERNETDATA_EXPECTED_VERSION");
        // The runner sets this to the published version it selected. Absent, someone ran
        // `dotnet test` by hand and the check has nothing to compare against.
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(expected),
            "INTERNETDATA_EXPECTED_VERSION is not set, so this run picked its own version");

        Assert.Equal(expected, Resolved().Version);
        Console.WriteLine($"testing InternetData {Resolved().Version} against {Staging.BaseUrl}");
    }

    // The deps.json keys each library "<name>/<version>" and records the type alongside. Read into
    // a record rather than held as JsonElements: a JsonDocument owns its buffer and every element
    // read after it is disposed throws.
    private static Library Resolved()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) + ".deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (!library.Name.StartsWith("InternetData/", StringComparison.Ordinal))
            {
                continue;
            }
            return new Library(
                library.Name.Split('/', 2)[1],
                library.Value.GetProperty("type").GetString(),
                library.Value.TryGetProperty("sha512", out var digest) ? digest.GetString() : null);
        }
        throw new InvalidOperationException($"{path} names no InternetData dependency at all");
    }

    private sealed record Library(string Version, string? Type, string? Sha512);
}
