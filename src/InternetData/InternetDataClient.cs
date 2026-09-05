namespace InternetData;

/// <summary>
/// A client for the InternetData API.
/// </summary>
/// <remarks>
/// <para>Build one and keep it: it owns a connection pool that is wasted if it is rebuilt per
/// request. It is safe to use from several threads at once.</para>
/// <para>Every endpoint is authenticated, so a key is required. Which databases a key can see is
/// decided by the API, not here.</para>
/// </remarks>
public sealed class InternetDataClient : IDisposable
{
    /// <summary>The production API, used when no other base URL is configured.</summary>
    public const string DefaultBaseUrl = "https://internetdata.io";

    // One chunk of a transfer, and therefore the ceiling on what a download of any size costs in
    // memory.
    private const int ChunkBytes = 64 * 1024;

    private readonly WireClient wire;
    private readonly HttpClient transfer;
    private readonly HttpClient? ownedHttpClient;
    private readonly int retries;

    /// <summary>A client with every default, on the key you were issued.</summary>
    public InternetDataClient(string apiKey)
        : this(new InternetDataClientOptions { ApiKey = apiKey }, null)
    {
    }

    /// <summary>A client that owns its <see cref="HttpClient"/>.</summary>
    public InternetDataClient(InternetDataClientOptions options)
        : this(options ?? throw new ArgumentNullException(nameof(options)), null)
    {
    }

    /// <summary>
    /// A client that borrows an <see cref="HttpClient"/>, which is how this plays with
    /// <c>IHttpClientFactory</c> and typed-client registration.
    /// </summary>
    /// <remarks>
    /// The supplied client MUST NOT follow redirects, or <see cref="DownloadUrlAsync"/> would fetch
    /// a database that routinely runs to gigabytes instead of returning its link. Register it with
    /// <c>.ConfigurePrimaryHttpMessageHandler(() =&gt; new HttpClientHandler { AllowAutoRedirect = false })</c>.
    /// A borrowed client is never disposed by this one, and its timeout and default headers are
    /// left alone.
    /// </remarks>
    public InternetDataClient(HttpClient httpClient, InternetDataClientOptions? options = null)
        : this(options ?? new InternetDataClientOptions(),
               httpClient ?? throw new ArgumentNullException(nameof(httpClient)))
    {
    }

    private InternetDataClient(InternetDataClientOptions o, HttpClient? supplied)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(o.Retries, nameof(o.Retries));
        // Not a 401 at request time: this API serves nothing anonymously, so an absent key is a
        // configuration mistake and worth reporting where it was made.
        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            throw new ArgumentException(
                "an API key carrying the db.download scope is required", nameof(o.ApiKey));
        }

        var http = supplied ?? o.HttpClient;
        if (http is null)
        {
            // Redirects OFF: unlike the JDK's client, .NET's follows them by default, and the
            // download endpoint answers 302 with the link this library exists to hand back.
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = o.RequestTimeout,
            };
            this.ownedHttpClient = http;
        }

        this.wire = new WireClient(http) { BaseUrl = o.BaseUrl, ApiKey = o.ApiKey };
        this.transfer = http;
        this.retries = o.Retries;
    }

    /// <summary>
    /// The published catalog, with your organization's license beside each entry.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Database.Standing"/> says whether a database is yours today
    /// (<c>licensed</c>), was (<c>expired</c>), or has never been bought (<c>unlicensed</c>). A
    /// license covers a FAMILY while a download names one of its versions, so the ids
    /// <see cref="DownloadAsync"/> and <see cref="ChecksumsAsync"/> take come from
    /// <see cref="Database.Versions"/>.</para>
    /// <para>A database commissioned for a single customer is ABSENT from this list for everyone
    /// else, rather than present as <c>unlicensed</c>. The catalog therefore differs between keys,
    /// and this answer is the only place it can be read.</para>
    /// </remarks>
    public Task<IReadOnlyList<Database>> ListAsync(CancellationToken cancellationToken = default)
        => Wire.ExecuteAsync(
            retries,
            async ct => (await wire.ListDatabasesAsync(ct).ConfigureAwait(false)).Databases,
            cancellationToken);

    /// <summary>
    /// What is inside one database: schema, sample rows, row count and sizes per format.
    /// </summary>
    /// <remarks>
    /// Carries <see cref="DatabaseMetadata.Updated"/> and <see cref="DatabaseMetadata.Entries"/>,
    /// so it answers whether today's build is worth fetching without downloading anything, and
    /// <see cref="DatabaseMetadata.Size"/>, which is how a caller budgets a transfer before
    /// starting one.
    /// </remarks>
    public Task<DatabaseMetadata> MetadataAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Wire.ExecuteAsync(retries, ct => wire.DatabaseMetadataV2Async(id, ct), cancellationToken);
    }

    /// <summary>
    /// The digests of one published file, to verify a download.
    /// </summary>
    /// <remarks>
    /// The whole set is returned rather than one digest: which ones a database publishes is the
    /// API's choice, not this library's.
    /// </remarks>
    public Task<DatabaseChecksums> ChecksumsAsync(
        string id, DatabaseFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Wire.ExecuteAsync(
            retries,
            async ct => (await wire.DatabaseChecksumV2Async(id, format, ct).ConfigureAwait(false)).Checksums,
            cancellationToken);
    }

    /// <summary>Your organization's recent download attempts, newest first.</summary>
    /// <remarks>
    /// Refusals are listed too, which is what answers "it stopped working" when nothing succeeded.
    /// </remarks>
    public Task<IReadOnlyList<Download>> DownloadsAsync(
        int? limit = null, CancellationToken cancellationToken = default)
        => Wire.ExecuteAsync(
            retries,
            async ct => (await wire.ListDownloadsAsync(limit, ct).ConfigureAwait(false)).Downloads,
            cancellationToken);

    /// <summary>
    /// The time-limited URL for one database file.
    /// </summary>
    /// <remarks>
    /// The API answers <c>302</c> to object storage. The URL is returned rather than the bytes so
    /// the caller decides how to transfer a file that routinely runs to gigabytes; it carries its
    /// own authorization, so it can be handed to a downloader that never sees your key. The link
    /// authorizes the START of a transfer, so one already running is not interrupted when it lapses.
    /// </remarks>
    public Task<string> DownloadUrlAsync(
        string id, DatabaseFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Wire.ExecuteAsync(retries, async ct =>
        {
            try
            {
                await wire.DownloadDatabaseV2Async(id, format, ct).ConfigureAwait(false);
            }
            catch (WireException e) when (e.StatusCode == 302)
            {
                // The generated method treats any non-2xx as a failure, so the SUCCESS case for
                // this endpoint arrives as an exception carrying the Location header.
                var location = LocationOf(e);
                if (location is null)
                {
                    throw new InternetDataException(
                        ErrorKind.ServerError, "the API redirected without a Location header", 302, null, e);
                }
                return location;
            }
            throw new InternetDataException(
                ErrorKind.ServerError, "expected a redirect to object storage", null, null, null);
        }, cancellationToken);
    }

    /// <summary>
    /// Download one database file to <paramref name="path"/>, and answer how many bytes arrived.
    /// </summary>
    /// <remarks>
    /// <para>The bytes land in a neighboring <c>.part</c> file that is renamed on completion, so a
    /// transfer that dies half way leaves nothing that reads as a whole database. Nothing beyond a
    /// single chunk is ever held in memory, whatever the database weighs.</para>
    /// <para>A failure DURING the transfer arrives as it happened, an <see cref="IOException"/>
    /// rather than an <see cref="InternetDataException"/>: a reset socket and a full disk are
    /// different problems, and only one of them is ours.</para>
    /// </remarks>
    public async Task<long> DownloadAsync(
        string id, DatabaseFormat format, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var response = await FetchAsync(id, format, cancellationToken).ConfigureAwait(false);
        var partial = path + ".part";
        try
        {
            long written;
            var file = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                written = await CopyAsync(response, file, cancellationToken).ConfigureAwait(false);
            }
            File.Move(partial, path, overwrite: true);
            return written;
        }
        catch
        {
            // The stream is closed by the time this runs, so the half-written file can go. Best
            // effort: the failure a caller needs to see is the one that got us here.
            try
            {
                File.Delete(partial);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    /// <summary>Download one database file and hand back its bytes.</summary>
    /// <remarks>
    /// <b>This holds the entire file in memory</b>, and the catalog spans seven orders of
    /// magnitude: <c>bogon_asn_v1</c> is 264 bytes while the largest published database is several
    /// gigabytes, past which a single array is not even allocatable. Reach for this at the small
    /// end, where the bytes go straight into a parser; use <see cref="DownloadAsync"/> for anything
    /// you have not checked against <see cref="MetadataAsync"/>.
    /// </remarks>
    public async Task<byte[]> DownloadBytesAsync(
        string id, DatabaseFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);

        using var response = await FetchAsync(id, format, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = body.ConfigureAwait(false);

        var declared = response.Content.Headers.ContentLength;
        if (declared is not (> 0 and <= int.MaxValue))
        {
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, ChunkBytes, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        // Allocated once from the declared length: a MemoryStream grows by doubling, so on a large
        // database the final grow alone costs twice the file. ReadExactlyAsync is also the
        // short-read guard, throwing EndOfStreamException rather than handing back a part-filled
        // array.
        var bytes = new byte[(int)declared.Value];
        await body.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Releases the <see cref="HttpClient"/>, if this client created it.</summary>
    public void Dispose() => ownedHttpClient?.Dispose();

    // Follows the 302 as a SECOND request rather than by loosening the redirect guard: the
    // presigned URL authorizes itself, so forwarding the API key would hand a credential to a host
    // with no business holding it. The key rides WireClient.PrepareRequest, which this request does
    // not go through. .NET drops Authorization across a cross-host redirect too, so this is belt
    // and braces rather than the only thing standing between the key and object storage.
    private async Task<HttpResponseMessage> FetchAsync(
        string id, DatabaseFormat format, CancellationToken cancellationToken)
    {
        var url = await DownloadUrlAsync(id, format, cancellationToken).ConfigureAwait(false);
        return await Wire.ExecuteAsync(retries, async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // ResponseHeadersRead is load-bearing, not an optimization. Under the default the whole
            // body is read inside SendAsync, and HttpClient.Timeout covers all of it, so a 30
            // second client would abandon any database that takes longer than that to move. With
            // headers-only the timeout stops at the response head.
            var response = await transfer
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }
            // Left unread: the status is what separates a lapsed link from a refused one, and
            // nothing bounds the size of an error body.
            var status = (int)response.StatusCode;
            var retryAfter = Wire.RetryAfterOf(response.Headers);
            response.Dispose();
            throw new InternetDataException(
                Wire.KindOf(status, retryAfter),
                $"object storage refused the download link with status {status}",
                status,
                retryAfter);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> CopyAsync(
        HttpResponseMessage response, Stream destination, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = body.ConfigureAwait(false);

        var buffer = new byte[ChunkBytes];
        long written = 0;
        int read;
        // A transfer that dies mid-body is HttpClient's to notice, and it does: a Content-Length
        // that outruns the socket raises HttpIOException(ResponseEnded) here rather than ending the
        // loop, so a short file cannot reach the rename above.
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
        }
        return written;
    }

    private static string? LocationOf(WireException e)
    {
        foreach (var header in e.Headers)
        {
            if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value?.FirstOrDefault();
            }
        }
        return null;
    }
}
