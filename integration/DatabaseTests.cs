using System.Security.Cryptography;

using Xunit;

namespace InternetData.Integration;

// The whole API surface, against a real deployment on a real license.
//
// The transfer is budgeted before it starts. Metadata publishes a size per format, and that size
// is checked against the ceiling below FIRST, so a mistaken database id can never quietly pull one
// of the multi-gigabyte databases through CI.
public class DatabaseTests
{
    // The smallest published database, and one the CI organization licenses.
    private const string DatabaseId = "bogon_ip_v1";

    private const DatabaseFormat Format = DatabaseFormat.Csvgz;

    // 8 MiB against a database published in hundreds of bytes. Four orders of magnitude of
    // headroom, so tripping it means the suite is pointed somewhere unintended, which is exactly
    // when a transfer must not proceed. The catalog reaches 5.34 GiB, so the ceiling is not
    // theoretical.
    private const long Ceiling = 8 << 20;

    private static readonly SemaphoreSlim TransferLock = new(1, 1);
    private static Transfer? shared;

    [Fact]
    public async Task TheCatalogAnswersTheSchemaTheClientWasGeneratedFrom()
    {
        var (client, recorder) = Staging.Client();
        using (client)
        {
            var databases = await client.Database.ListAsync();

            Assert.NotEmpty(databases);
            var licensed = databases
                .Where(d => d.Standing == DatabaseStanding.Licensed)
                .Select(d => d.Base)
                .Order()
                .ToArray();
            Console.WriteLine($"licensed: {string.Join(", ", licensed)}");
            foreach (var want in Staging.LicensedBases)
            {
                Assert.Contains(want, licensed, StringComparer.Ordinal);
            }
            // The catalog is a discovery surface, not a license list: an unlicensed database is
            // still listed, which is what makes `standing` worth reading. A private one is absent
            // instead, and nothing here may assume otherwise.
            Assert.Contains(databases, d => d.Standing != DatabaseStanding.Licensed);

            foreach (var family in databases)
            {
                Assert.False(string.IsNullOrEmpty(family.Base), "a listed family carries no base");
                Assert.False(string.IsNullOrEmpty(family.Name), $"{family.Base} carries no name");
                Assert.True(
                    Enum.IsDefined(family.Standing), $"{family.Base} carries an undocumented standing");
                Assert.True(
                    family.LicenseType is null || Enum.IsDefined(family.LicenseType.Value),
                    $"{family.Base} carries an undocumented license_type term");
                // A license covers the family, and these are the ids the download and checksum
                // calls take.
                Assert.True(family.Versions.Count > 0, $"{family.Base} carries no versions");
                foreach (var version in family.Versions)
                {
                    Assert.False(string.IsNullOrEmpty(version.Id), $"{family.Base} has a version with no id");
                    Assert.True(version.Formats.Count > 0, $"{version.Id} carries no formats");
                }
            }
            // An unsent key answers 401, so a listing at all proves the key reached the wire; this
            // says it went as a header rather than in the URL, where a proxy log would keep it.
            Assert.True(recorder.CarriedKey, "the key never reached the wire");
            Assert.All(recorder.Seen, fact => Assert.Equal(Staging.BaseUrl, fact.Origin));
        }
    }

    [Fact]
    public async Task ADatabaseTheOrganizationDoesNotLicenseIsRefusedCleanly()
    {
        var (client, recorder) = Staging.Client();
        using (client)
        {
            var error = await Assert.ThrowsAsync<InternetDataException>(
                () => client.Database.DownloadUrlAsync(Staging.UnlicensedId, Format));

            Assert.Equal(ErrorKind.Forbidden, error.Kind);
            Assert.Equal(403, error.StatusCode);
            Assert.False(error.Retryable, "a license refusal is not worth retrying");
            // The API says WHICH refusal this is (`{"rc":"NOT_LICENSED"}`). Falling back to the
            // status means the client never read the envelope.
            Assert.False(
                error.Message.StartsWith("request failed with status", StringComparison.Ordinal),
                $"Message = {error.Message}, which is the client fallback, so the body went unread");
            Assert.Single(recorder.Seen);
        }
    }

    [Fact]
    public async Task DownloadStreamsARealDatabaseToDiskIntact()
    {
        var transfer = await Transferred();

        Assert.True(transfer.Written > 0, "nothing was transferred");
        Assert.Equal(transfer.Written, new FileInfo(transfer.Path).Length);
        Assert.False(File.Exists(transfer.Path + ".part"), "the .part file outlived a successful transfer");
        var body = await File.ReadAllBytesAsync(transfer.Path);
        Assert.True(body.Length > 2 && body[0] == 0x1f && body[1] == 0x8b, "the payload is not gzip");

        Assert.True(
            transfer.Checksums.Sha256?.Length == 64,
            $"sha256 = {transfer.Checksums.Sha256}, so the checksums did not unwrap past the envelope");
        Assert.Equal(transfer.Checksums.Sha256, Digest(body));

        // The presigned URL authorizes itself, so the request that follows the 302 must carry no
        // credential.
        var storage = transfer.Facts.Where(fact => fact.Origin != Staging.BaseUrl).ToArray();
        Assert.True(storage.Length > 0, "nothing was fetched from object storage, so no 302 was followed");
        foreach (var fact in storage)
        {
            Assert.False(fact.CarriedKey, $"the API key was sent to object storage at {fact.Origin}");
        }
    }

    [Fact]
    public async Task DownloadUrlHandsBackACredentialFreeLinkThatWorksOnItsOwn()
    {
        var transfer = await Transferred();
        var (client, _) = Staging.Client();
        using (client)
        {
            var url = await client.Database.DownloadUrlAsync(DatabaseId, Format);

            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            // Asserted as a boolean rather than with DoesNotContain, which prints the operand it
            // searched for. These logs are public.
            Assert.False(
                url.Contains(Staging.Key, StringComparison.Ordinal),
                "the presigned link carries the API key");
            // Fetched with a bare HttpClient that has never heard of the key: the link is the whole
            // credential, which is what makes it safe to hand to a downloader.
            using var bare = new HttpClient();
            var bytes = await bare.GetByteArrayAsync(url);
            Assert.Equal(transfer.Checksums.Sha256, Digest(bytes));
        }
    }

    [Fact]
    public async Task DownloadBytesAgreesWithTheStreamedCopy()
    {
        var transfer = await Transferred();
        var (client, _) = Staging.Client();
        using (client)
        {
            var raw = await client.Database.DownloadBytesAsync(DatabaseId, Format);

            Assert.Equal(transfer.Written, raw.LongLength);
            Assert.Equal(transfer.Checksums.Sha256, Digest(raw));
        }
    }

    // Measured 2026-09-05, and the reason this asserts only the successful half: a gate refusal
    // is recorded against no organization at all, so it never appears in that organization's own
    // history however soon it is read. Asserting its presence would fail against the API as it
    // stands; asserting its absence would pin behavior that contradicts the endpoint's own
    // documentation.
    [Fact]
    public async Task TheDownloadHistoryRecordsTheTransfersThisRunMade()
    {
        await Transferred();
        var (client, _) = Staging.Client();
        using (client)
        {
            var attempts = await client.Database.DownloadsAsync(50);

            Assert.NotEmpty(attempts);
            Assert.All(attempts, a => Assert.True(
                Enum.IsDefined(a.Outcome), $"{a.DatasetId} carries an undocumented outcome"));
            // Projected before asserting, so a failure prints ids and outcomes rather than every
            // field of every attempt into a public log.
            var seen = attempts.Select(a => $"{a.DatasetId}:{a.Outcome}").ToArray();
            Assert.Contains($"{DatabaseId}:{DownloadOutcome.Ok}", seen, StringComparer.Ordinal);
            Assert.All(attempts, a => Assert.True(a.Created > DateTimeOffset.UtcNow.AddYears(-5)));
        }
    }

    // Memoized so the transfer tests share one download rather than pulling the database several
    // times. Held in a directory of its own, because it outlives whichever test asked first.
    private static async Task<Transfer> Transferred()
    {
        await TransferLock.WaitAsync();
        try
        {
            if (shared is not null)
            {
                return shared;
            }
            var (client, recorder) = Staging.Client();
            using (client)
            {
                var metadata = await client.Database.MetadataAsync(DatabaseId);
                Assert.Equal(DatabaseId, metadata.Id);
                Assert.True(metadata.Entries > 0, $"{DatabaseId} publishes no row count");
                var size = PublishedSize(metadata);
                Assert.True(
                    size > 0 && size <= Ceiling,
                    $"{DatabaseId} is {size} bytes, past the {Ceiling} ceiling, so it is not transferred");

                var dir = Directory.CreateTempSubdirectory("internetdata-integration-").FullName;
                var path = Path.Combine(dir, DatabaseId + ".csv.gz");
                var written = await client.Database.DownloadAsync(DatabaseId, Format, path);
                // Read after the transfer, so a rebuild between the two calls shows up as a digest
                // mismatch rather than passing against a digest of nothing.
                var checksums = await client.Database.ChecksumsAsync(DatabaseId, Format);
                Console.WriteLine($"{DatabaseId}.{Format}: {written} bytes, metadata says {size}");

                shared = new Transfer(written, path, checksums, recorder.Seen);
                return shared;
            }
        }
        finally
        {
            TransferLock.Release();
        }
    }

    private static long PublishedSize(DatabaseMetadata metadata)
    {
        Assert.True(
            metadata.Size.TryGetValue(Format.ToString().ToLowerInvariant(), out var size),
            $"{DatabaseId} publishes no {Format} size to check a transfer against");
        return size;
    }

    private static string Digest(byte[] body)
        => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    private sealed record Transfer(
        long Written, string Path, DatabaseChecksums Checksums, IReadOnlyList<Fact> Facts);
}
