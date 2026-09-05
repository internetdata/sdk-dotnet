# [<img src="https://docs.internetdata.io/logo.svg" alt="InternetData" width="24"/>](https://internetdata.io/) InternetData .NET Client Library

[![NuGet](https://img.shields.io/nuget/v/InternetData.svg)](https://www.nuget.org/packages/InternetData)
[![license](https://img.shields.io/nuget/l/InternetData.svg)](LICENSE)

The official .NET client library for the [InternetData](https://internetdata.io) API.

The library downloads the IP, ASN and domain databases your organization is licensed for, and gives you their build metadata, checksums and download history along the way.

## Getting Started

```bash
dotnet add package InternetData
```

Targets .NET 8 and newer.

## Usage

Every endpoint is authenticated, so start with a key carrying the `db.download` scope:

```csharp
using InternetData;

using var client = new InternetDataClient(Environment.GetEnvironmentVariable("INTERNETDATA_API_KEY")!);

foreach (var db in await client.ListAsync())
{
    Console.WriteLine($"{db.Base}: {db.Standing}");   // bogon_ip: Licensed
}
```

A license covers a database FAMILY, while a download names one of its versions, so the ids you pass to the other calls come from `Versions`:

```csharp
var family = (await client.ListAsync()).First(db => db.Standing == DatabaseStanding.Licensed);
var id = family.Versions[^1].Id;              // "bogon_ip_v1"
var formats = family.Versions[^1].Formats;    // [Csvgz, Mmdb]
```

### Downloading a database

`DownloadAsync` streams to a path, so nothing bigger than a chunk is ever held in memory:

```csharp
var written = await client.DownloadAsync(id, DatabaseFormat.Csvgz, $"{id}.csv.gz");
```

The bytes land in a neighboring `.part` file that is renamed on completion, so a transfer that dies half way leaves nothing that reads as a whole database.

For a small database, take the bytes directly:

```csharp
var bytes = await client.DownloadBytesAsync("bogon_asn_v1", DatabaseFormat.Csvgz);
```

This holds the whole file in memory, and the catalog spans seven orders of magnitude, so check `MetadataAsync` first for anything you have not measured.

### Handing the transfer to something else

The API answers a download with a `302` to a time-limited URL that carries its own authorization, so `DownloadUrlAsync` gives you a link you can pass to a downloader, a job queue or another machine without passing your key along with it:

```csharp
var url = await client.DownloadUrlAsync(id, DatabaseFormat.Mmdb);
```

The library follows nothing: you get the link, not the file. The link authorizes the START of a transfer, so one already running is not interrupted when it lapses.

Because a `302` is the answer, an `HttpClient` you supply yourself must not follow redirects, or it would fetch the whole database in place of the link. A client that does follow them is refused with a clear error rather than quietly downloading gigabytes.

### Is today's build worth fetching?

`MetadataAsync` carries the build date, the row count and a size per format, without downloading anything:

```csharp
var metadata = await client.MetadataAsync(id);
Console.WriteLine($"{metadata.Updated} {metadata.Entries} rows, {metadata.Size["csvgz"]} bytes");
Console.WriteLine(metadata.UpdateFreq);          // "daily"
```

### Verifying what arrived

```csharp
var sums = await client.ChecksumsAsync(id, DatabaseFormat.Csvgz);
Console.WriteLine(sums.Sha256);
```

### What has been downloaded

Your organization's recent attempts, newest first. Refusals are listed too, which is what answers "it stopped working":

```csharp
foreach (var attempt in await client.DownloadsAsync(limit: 20))
{
    Console.WriteLine($"{attempt.Created:u} {attempt.DatasetId} {attempt.Outcome} {attempt.Bytes}");
}
```

### Errors

Failures throw an `InternetDataException` carrying a `Kind` and a `Retryable` flag:

```csharp
try
{
    await client.MetadataAsync(id);
}
catch (InternetDataException e)
{
    Console.Error.WriteLine($"{e.Kind} {e.Retryable} {e.StatusCode} {e.Message}");
}
```

`Kind` is one of `BadRequest`, `Unauthorized`, `Forbidden`, `RateLimited`, `QuotaExceeded`, `ServerError` or `Network`. `Message` is the API's own reason code, so a license refusal reads `NOT_LICENSED` rather than `403`.

Note that `RateLimited` and `QuotaExceeded` both arrive as HTTP 429 and are not the same thing. A rate limit is when the API faces extreme traffic bursts and so retrying later works; but a spent quota needs your allowance raised or the window to roll over. The library retries rate limits for you, but not if your quota is exceeded.

### Dependency injection

The client takes an `HttpClient`, so it registers as a typed client and picks up your handler pipeline, pooling and resilience policies:

```csharp
services.AddSingleton(new InternetDataClientOptions { ApiKey = builder.Configuration["InternetData:ApiKey"] });
services.AddHttpClient<InternetDataClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
```

### What you can see

`ListAsync` returns the catalog as your organization is entitled to see it, and a database commissioned for a single customer is simply absent from everyone else's, rather than listed as `Unlicensed`. So the catalog is not the same for every key, and this call is the only place it can be read.

## Other Libraries

There are official InternetData client libraries available for many languages including PHP, Python, Go, Java, Ruby, and many popular frameworks such as Django, Rails, and Laravel. See our GitHub at https://github.com/internetdata for more.

## About InternetData

IP, ASN and Domain data to reveal unique insights about the internet. APIs, Databases and Live Feeds available.

[<img src="https://docs.internetdata.io/logo.svg" alt="InternetData" width="96"/>](https://internetdata.io/)

## License

This project is licensed under the [MIT License](LICENSE).
