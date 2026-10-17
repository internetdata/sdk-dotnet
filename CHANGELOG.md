# Changelog

What each release changed for you, newest first. Each line is a commit's summary, linked to its full description and diff. Releases before 2.1.1 are described by their release commits.

## 2.5.0 - 2026-10-17

### Features

- Re-pin the spec to 2026.10.16: dynamic client registration ([`ea5305e`](https://github.com/internetdata/sdk-dotnet/commit/ea5305e76c635a3ad3dcfcb18af083b0d53b45d7))

## 2.4.8 - 2026-10-16

### Fixes

- Refuse an API key holding a character outside ASCII where it is set ([`db83014`](https://github.com/internetdata/sdk-dotnet/commit/db83014b4572e56ef69f3cbf31a6936ebb8854b2))

## 2.4.7 - 2026-10-16

### Fixes

- Re-pin the spec to 2026.10.15: three descriptions in US spelling ([`b3056a5`](https://github.com/internetdata/sdk-dotnet/commit/b3056a50667222d580d95421b1b486d61292c8b5))

## 2.4.6 - 2026-10-14

### Fixes

- Retry an entry missing a member, the link's 200 and an OAuth 2xx ([`201c446`](https://github.com/internetdata/sdk-dotnet/commit/201c446ba6598db5c9dcfb08716b66c9a1ca2d2e))

## 2.4.5 - 2026-10-13

### Fixes

- Re-pin the spec to 2026.10.13: Open databases come with a license ([`ec13e72`](https://github.com/internetdata/sdk-dotnet/commit/ec13e7268b637612bdfeea3c2bcd21562e621480))

## 2.4.4 - 2026-10-12

### Fixes

- Fail an empty object from list, downloads or checksums ([`33fee55`](https://github.com/internetdata/sdk-dotnet/commit/33fee5579c235c52fc25f268045a33704bd63f11))

## 2.4.3 - 2026-10-11

### Fixes

- Retry a 200 the call cannot read, carrying its status ([`da22cf9`](https://github.com/internetdata/sdk-dotnet/commit/da22cf936b8eff477b5305816571052db7db6a76))

## 2.4.2 - 2026-10-11

### Fixes

- Read Retry-After as digits or an RFC 9110 date in GMT ([`2cca22c`](https://github.com/internetdata/sdk-dotnet/commit/2cca22c00000688a14018301a144b0006c17cfb2))
- Trim the API key, and refuse one holding a control character ([`857e676`](https://github.com/internetdata/sdk-dotnet/commit/857e6767d2217bc9e37c151b65f92ebf0b45e123))

## 2.4.1 - 2026-10-10

### Fixes

- Re-pin the spec to 2026.10.09: rotating a key needs apikeys.reveal ([`1f92821`](https://github.com/internetdata/sdk-dotnet/commit/1f9282149525338ad88911648cd30d172d1edf17))

## 2.4.0 - 2026-10-09

### Features

- Re-pin the spec to 2026.10.08, adding the Open databases' open flag ([`ee3bb8b`](https://github.com/internetdata/sdk-dotnet/commit/ee3bb8b97efd9a022bd33392dd05ece456f4acec))

## 2.3.2 - 2026-10-04

### Fixes

- End the poll's sleep at its deadline, in parts past Task.Delay's ceiling ([`d5b6b8c`](https://github.com/internetdata/sdk-dotnet/commit/d5b6b8cec5903f352b7988dcc79ef6c6c3145f28))
- Re-pin the spec to 2026.10.03: metadata needs no license ([`d9e8029`](https://github.com/internetdata/sdk-dotnet/commit/d9e8029531e40a571f7b5c76d77d99189cc5e4bf))

## 2.3.1 - 2026-10-01

### Fixes

- Reserve DownloadBytesAsync's array as the bytes arrive ([`785dd3a`](https://github.com/internetdata/sdk-dotnet/commit/785dd3a7011e25ece3bde43ce1a437129997752c))

## 2.3.0 - 2026-09-30

### Features

- Add the authorization code sign-in, with PKCE ([`5a61dde`](https://github.com/internetdata/sdk-dotnet/commit/5a61dde9a06ff5d00e0b04df74d51165a3bebb60))

## 2.2.0 - 2026-09-27

### Features

- Re-pin the spec to 2026.09.26, adding its evaluation-sample fields ([`3d3cc4b`](https://github.com/internetdata/sdk-dotnet/commit/3d3cc4be7b1d1ae48527d2d62f74270974c49222))

## 2.1.1 - 2026-09-22

### Fixes

- Wait out a Retry-After too long to count, and drop every trailing slash ([`58a4f08`](https://github.com/internetdata/sdk-dotnet/commit/58a4f08de7b436704c17a6f7774f3f59e016f05a))
