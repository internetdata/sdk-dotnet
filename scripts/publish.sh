#!/bin/bash

# Publishes the package to NuGet from inside the official .NET SDK image, so a release needs
# nothing installed locally beyond docker and works identically on any machine. The release
# workflow does the same steps on a tag; this is the manual path for a first release or when
# Actions is not an option.
#
#   NUGET_API_KEY=... ./scripts/publish.sh            # build, test, pack, push
#   NUGET_API_KEY=... DRY_RUN=1 ./scripts/publish.sh  # everything except the push
#
# nuget.org attaches a trusted-publishing policy to an EXISTING package, so the first release has
# to use an API key from https://www.nuget.org/account/apikeys. After that the workflow can take
# the OIDC path and the key can be revoked.
#
# The SDK 10 image carries no .NET 8 runtime, so the net8.0 test binary is rolled forward onto the
# runtime that IS there. That also exercises the library on the current LTS, which is where most
# consumers will load it.

set -euo pipefail

cd "$(dirname "$0")/.."

: "${NUGET_API_KEY:?set NUGET_API_KEY to a key that may push this package}"

SDK_IMAGE="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
NUGET_DIR="${NUGET_DIR:-${HOME}/.nuget/packages}"
DRY_RUN="${DRY_RUN:-}"

mkdir -p "$NUGET_DIR"

# artifacts, bin and obj are anonymous volumes: the container builds as root, and without them it
# leaves root-owned output in the working tree that the next host-side build cannot overwrite. The
# package cache is mounted from the HOST instead, so restores stay warm across runs and no cache
# ever lands inside the repo.
docker run --rm \
    -v "$PWD:/w" \
    -v "$NUGET_DIR:/root/.nuget/packages" \
    -v /w/artifacts \
    -v /w/src/InternetData/bin -v /w/src/InternetData/obj \
    -v /w/tests/InternetData.Tests/bin -v /w/tests/InternetData.Tests/obj \
    -w /w \
    -e NUGET_API_KEY \
    -e DRY_RUN="$DRY_RUN" \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e DOTNET_ROLL_FORWARD=Major \
    "$SDK_IMAGE" bash -euc '
        dotnet test -c Release
        dotnet pack src/InternetData/InternetData.csproj -c Release -o artifacts
        ls -l artifacts
        if [ -n "$DRY_RUN" ] ; then
            echo "DRY_RUN set, not pushing"
            exit 0
        fi
        dotnet nuget push "artifacts/*.nupkg" \
            --source https://api.nuget.org/v3/index.json \
            --api-key "$NUGET_API_KEY" \
            --skip-duplicate
    '
