# Changelog

What each release changed for you, newest first. Each line is a commit's summary, linked to its full description and diff. Releases before 2.1.1 are described by their release commits.

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
