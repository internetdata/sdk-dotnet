# [<img src="https://s3.internetdata.io/internetdata-public/brand/mark.svg" alt="InternetData" height="28"/>](https://internetdata.io/) InternetData .NET Client Library

[![NuGet](https://img.shields.io/nuget/v/InternetData.svg)](https://www.nuget.org/packages/InternetData)
[![license](https://img.shields.io/nuget/l/InternetData.svg)](LICENSE)

The official .NET client library for the [InternetData](https://internetdata.io) API.

It tells you what every InternetData database says about an IP address, an AS or a domain, and downloads the IP and ASN databases your organization is licensed for, with their build metadata, checksums and download history.

## Getting Started

```bash
dotnet add package InternetData
```

Targets .NET 8 and newer.

## Usage

**No API key needed to start.** Unlimited, the free plan, answers the anycast, carrier, satellite and service flags, Tor and relay detection, the country, the AS, the company, the reverse DNS name and the abuse email, with no daily or monthly cap, with a key or without one.

```csharp
using InternetData;

using var client = new InternetDataClient();

var result = await client.LookupAsync("8.8.8.8");
Console.WriteLine(result.IsAnycast);          // True
Console.WriteLine(result.Location.Country);   // US
Console.WriteLine(result.Asn.Name);           // Google LLC
```

### With an API key

Every paid plan answers every field: the VPN, proxy, hosting and CDN flags as well, the row behind each flag, the city and coordinates, the Whois and RWhois records and how many domains an address hosts. The plans differ only in how many lookups they include, and a free account tries every field, 100 lookups a day. Create a key in the [console](https://app.internetdata.io), then pass it in:

```csharp
using var client = new InternetDataClient(Environment.GetEnvironmentVariable("INTERNETDATA_API_KEY")!);

var result = await client.LookupAsync("146.70.116.130");
Console.WriteLine(result.IsVpn);              // True, on a paid plan
Console.WriteLine(result.Vpn?.Provider);      // mullvad, on a paid plan
Console.WriteLine(result.Location.City);      // Vienna, on a paid plan
```

### An AS or a domain

```csharp
var asn = await client.LookupAsnAsync(15169);
Console.WriteLine(asn.Name);           // Google LLC
Console.WriteLine(asn.Prefixes?.Count); // every prefix it announces, on a paid plan

var domain = await client.LookupDomainAsync("example.com");
Console.WriteLine(domain.IsRegistered); // True
Console.WriteLine(domain.Dns?.Mx.Count); // its MX records, on a paid plan
```

Neither is cached or batched, and both take the same per-call `LookupOptions` as `LookupAsync`.

### Your own address

```csharp
var result = await client.MyIpAsync();
Console.WriteLine(result.Ip);   // the address we saw this call come from
```

Same answer `LookupAsync` would give for that address, and the same cost against your allowance. It is deliberately not cached: which address you are is the whole question, and a machine that moves between networks would otherwise be told where it used to be.

### Your plan and usage

```csharp
var acct = await client.MyEntitlementAsync();
Console.WriteLine(acct.Plan.Key);         // the plan your key is on
Console.WriteLine(acct.Usage.Requests);   // lookups spent this window
Console.WriteLine(acct.Usage.WindowEnd);  // when the allowance resets
```

It needs a key, and it's never cached. `HardLimit` is `null` on an uncapped plan, which is not the same as zero.

### Batch lookup

Look up many addresses at once. Bogons and cached answers are handled locally, and everything else goes to the batch endpoint in chunks of up to 1000 addresses, in parallel. The batch endpoint needs a paid plan:

```csharp
var results = await client.LookupBatchAsync(new[] { "146.70.116.130", "8.8.8.8", "1.1.1.1" });

foreach (var (ip, answer) in results)
{
    if (!answer.IsSuccess)
    {
        Console.Error.WriteLine($"{ip}: {answer.Error!.Message}");
        continue;
    }
    Console.WriteLine($"{ip}: {answer.Result!.Asn.Name}");
}
```

Results are keyed by address, in the order you first listed each one, so duplicates in your list collapse into a single entry and one address failing never loses the rest: it carries its error as its value, with the status the API would have given that address on its own.

How many chunks are in flight at once, how many times a failed chunk is retried, and how long each attempt at a chunk may take, are configurable per call:

```csharp
var results = await client.LookupBatchAsync(manyIps, new BatchOptions
{
    Concurrency = 4,
    Retries = 4,
    RequestTimeout = TimeSpan.FromSeconds(10),
});
```

### Caching

Answers are cached by default, so repeat lookups of the same address are free:

```csharp
using var client = new InternetDataClient();

var result = await client.LookupAsync("8.8.8.8");
Console.WriteLine(result.Asn.Name);    // Google LLC, API request

var result2 = await client.LookupAsync("8.8.8.8");
Console.WriteLine(result2.Asn.Name);   // Google LLC, no API request, result was cached
```

You can change the default cache variables (max size, TTL, etc) on initialization, or even disable it:

```csharp
using var client = new InternetDataClient(new InternetDataClientOptions
{
    CacheSize = 50_000,
    CacheTtl = TimeSpan.FromHours(6),
});

using var noCache = new InternetDataClient(new InternetDataClientOptions { CacheEnabled = false });
```

The cache belongs to the client instance, never to the process, because two keys can be on different plans and so entitled to different fields.

### Private and reserved addresses

Private, loopback, link-local, documentation and multicast addresses (and their IPv6 equivalents, including the 6to4 and Teredo ranges) are in no database. The library answers them locally, so they cost no request and no quota:

```csharp
var result = await client.LookupAsync("192.168.1.1");
result.IsBogon;   // True
result.IsVpn;     // False
```

The check is available on the client, which is handy when your inputs are addresses anyway:

```csharp
client.IsBogon("10.0.0.1");   // True
client.IsBogon("8.8.8.8");    // False
```

It is also available on its own, if you want it without a client:

```csharp
InternetData.Bogon.IsBogon("10.0.0.1");   // True
```

### Database downloads

If your key carries the `db.download` scope, the databases your organization is licensed for are available through `client.Database`, which is where the sibling VPNDetection library keeps the same seven calls:

```csharp
foreach (var db in await client.Database.ListAsync())
{
    Console.WriteLine($"{db.Base}: {db.Standing}");   // bogon_ip: Licensed
}
```

A license covers a database FAMILY, while a download names one of its versions, so the IDs you pass to the other calls come from `Versions`:

```csharp
var family = (await client.Database.ListAsync()).First(db => db.Standing == Standing.Licensed);
var id = family.Versions[^1].Id;              // "bogon_ip_v1"
var formats = family.Versions[^1].Formats;    // [Csvgz, Mmdb]
```

#### Downloading a database

`DownloadAsync` streams to a path, so nothing bigger than a chunk is ever held in memory:

```csharp
var written = await client.Database.DownloadAsync(id, DatabaseFormat.Csvgz, $"{id}.csv.gz");
```

The bytes land in a neighboring `.part` file that is renamed on completion, so a transfer that dies half way leaves nothing that reads as a whole database.

For a small database, take the bytes directly:

```csharp
var bytes = await client.Database.DownloadBytesAsync("bogon_asn_v1", DatabaseFormat.Csvgz);
```

This holds the whole file in memory, and the catalog spans seven orders of magnitude, so check `MetadataAsync` first for anything you have not measured.

#### Handing the transfer to something else

The API answers a download with a `302` to a time-limited URL that carries its own authorization, so `DownloadUrlAsync` gives you a link you can pass to a downloader, a job queue or another machine without passing your key along with it:

```csharp
var url = await client.Database.DownloadUrlAsync(id, DatabaseFormat.Mmdb);
```

The library follows nothing: you get the link, not the file. The link authorizes the START of a transfer, so one already running is not interrupted when it lapses.

Because a `302` is the answer, an `HttpClient` you supply yourself must not follow redirects, or it would fetch the whole database in place of the link. A client that does follow them is refused with a clear error rather than quietly downloading gigabytes.

#### Downloading a named build

Each download call fetches the build that is live unless you name one. A `database.updated` webhook names the build it announces and carries its `sha256`, so a receiver that downloads after the next publish asks for that build to get the file the hash is of:

```csharp
await client.Database.DownloadAsync("vpn_ip_v1", DatabaseFormat.Mmdb, "./vpn_ip_v1.mmdb", build);
```

A build is kept 7 days. Past that the call throws `BadRequest` with the message `BUILD_GONE`, and downloading without a build fetches the live one.

#### Is today's build worth fetching?

`MetadataAsync` carries the build date, the row count and a size per format, without downloading anything:

```csharp
var metadata = await client.Database.MetadataAsync(id);
Console.WriteLine($"{metadata.Updated} {metadata.Entries} rows, {metadata.Size["csvgz"]} bytes");
Console.WriteLine(metadata.UpdateFreq);          // "daily"
```

#### Verifying what arrived

```csharp
var sums = await client.Database.ChecksumsAsync(id, DatabaseFormat.Csvgz);
Console.WriteLine(sums.Sha256);
```

#### What has been downloaded

Your organization's recent attempts, newest first. Refusals are listed too, which is what answers "it stopped working":

```csharp
foreach (var attempt in await client.Database.DownloadsAsync(limit: 20))
{
    Console.WriteLine($"{attempt.Created:u} {attempt.DatasetId} {attempt.Outcome} {attempt.Bytes}");
}
```

### Errors

Failures throw an `InternetDataException` carrying a `Kind` and a `Retryable` flag:

```csharp
try
{
    await client.LookupAsync("1.1.1.1");
}
catch (InternetDataException e)
{
    Console.Error.WriteLine($"{e.Kind} {e.Retryable} {e.StatusCode} {e.Message}");
}
```

`Kind` is one of `BadRequest`, `Unauthorized`, `Forbidden`, `RateLimited`, `QuotaExceeded`, `ServerError` or `Network`. `Message` is the API's own reason, so a license refusal reads `NOT_LICENSED` rather than `403`.

Note that `RateLimited` and `QuotaExceeded` both arrive as HTTP 429 and are not the same thing. A rate limit carries `Retry-After` and retrying later works, so the library retries it for you; a spent quota needs your allowance raised or the window to roll over, so it never does.

Each attempt is abandoned after 30 seconds by default (`RequestTimeout` on the options), which fails as a retryable `Network` error. One call can set its own, longer or shorter:

```csharp
var result = await client.LookupAsync("8.8.8.8", new LookupOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
```

### Dependency injection

Register one shared client. The answer cache lives in the client, so one built per request scope starts empty every time and pays for a lookup the last scope already made. In a web app, bound each lookup tightly too, so an unreachable API costs a request seconds rather than minutes:

```csharp
services.AddSingleton(new InternetDataClient(new InternetDataClientOptions
{
    ApiKey = builder.Configuration["InternetData:ApiKey"],
    RequestTimeout = TimeSpan.FromSeconds(2),
    Retries = 0,
}));
```

The client also takes an `HttpClient`, so it can register as a typed client and pick up your handler pipeline, pooling and resilience policies:

```csharp
services.AddSingleton(new InternetDataClientOptions { ApiKey = builder.Configuration["InternetData:ApiKey"] });
services.AddHttpClient<InternetDataClient>(http => http.Timeout = TimeSpan.FromSeconds(2))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
```

A typed client is transient, so each scope gets a new `InternetDataClient` with a cache of its own. A borrowed `HttpClient` keeps its own `Timeout`, 100 seconds unless you set one, so `RequestTimeout` on the options does not apply to it; a per-call `RequestTimeout` still does.

### What you can see

`ListAsync` returns the catalog as your organization is entitled to see it.

### Sign in with OAuth (device flow)

A program running on the person's own machine can let them sign in with a browser and pick one of their API keys, instead of asking them to paste it:

```csharp
using var client = new InternetDataClient();

var device = await client.Oauth.DeviceAuthorizationAsync(
    "your-client-id", new DeviceAuthorizationOptions { Scope = "account.read apikeys.read apikeys.reveal" });
Console.WriteLine($"Open {device.VerificationUri} and enter {device.UserCode}");

var token = await client.Oauth.PollDeviceTokenAsync("your-client-id", device);
if (token.Apikey is null)
{
    throw new InvalidOperationException("no API key came back: none was picked, or it can't be shown again");
}
using var keyed = new InternetDataClient(new InternetDataClientOptions { ApiKey = token.Apikey });
```

A denied sign-in throws `OauthAccessDeniedException` and a code that ran out `OauthExpiredTokenException`. Client IDs are issued on request from support@internetdata.io, and `client.Oauth.RevokeAsync("your-client-id", token.RefreshToken)` signs the machine out again.

### Sign in with OAuth (authorization code)

An app that can take a browser redirect signs the person in there instead, with a PKCE pair made for that one sign-in:

```csharp
using var client = new InternetDataClient();
var redirectUri = "http://127.0.0.1:8765/callback";
var pkce = client.Oauth.CreatePkce();

var url = client.Oauth.AuthorizationUrl("your-client-id", redirectUri, pkce.Challenge,
    new AuthorizationUrlOptions { Scope = "apikeys.use", State = "your-state" });
// Open url in the browser. Its redirect to redirectUri carries code and state.
var token = await client.Oauth.ExchangeAuthorizationCodeAsync("your-client-id", code, pkce.Verifier, redirectUri);
```

Check that `state` came back as you sent it before you exchange `code`, which works once. The client ID can also be the https URL of a client metadata document your app serves, and such an app is never handed a key, so `token.Apikey` stays `null`.

### Register a client

An app with no client ID, and no client metadata document to use as one, registers itself, then signs in with the `ClientId` it gets back as the client ID for `AuthorizationUrl` and `ExchangeAuthorizationCodeAsync`:

```csharp
using var client = new InternetDataClient();
var registration = await client.Oauth.RegisterAsync(new[] { "http://127.0.0.1:8765/callback" },
    new RegisterOptions { ClientName = "Your App" });
```

Each redirect URI must be https on a public host name, or http on a loopback address, which then matches any port. Nothing is kept until a person approves the client, so an app with no client ID saved can simply register again.

### Absent is not false

Unlimited leaves out the VPN, proxy, hosting and CDN flags, the city and coordinates, and the paid half of the company and abuse contact; a free-plan key's answer carries `TrialExpired`, true once its daily trial is spent. Those members are `null` on a plan without them, which means "not in your plan" rather than "checked, and no".

```csharp
result.IsHosting ?? false     // when you only want the flag
result.IsHosting is null      // on Unlimited: not in your plan
result.Location.City is null  // on Unlimited: not in your plan
result.TrialExpired == true   // a free-plan key's daily trial is spent
```

## Other Libraries

There are official InternetData client libraries available for many languages including PHP, Python, Go, Java, and Ruby. See our GitHub at https://github.com/internetdata for more.

## About InternetData

Geolocation, anonymity, ownership and network databases for IP addresses and AS numbers, licensed as files you download and query yourself.

[<img src="https://s3.internetdata.io/internetdata-public/brand/mark.svg" alt="InternetData" height="64"/>](https://internetdata.io/)

## License

This project is licensed under the [MIT License](LICENSE).
