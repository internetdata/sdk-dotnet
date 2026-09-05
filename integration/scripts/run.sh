#!/bin/bash

# Runs the integration suite against the package as PUBLISHED on nuget.org, which is the one thing
# the suite in ../tests cannot check: it builds this working tree, so it stays green through a tag
# that never landed, a package that ships no net8.0 asset, or an API a consumer cannot reach.
#
#   ./scripts/run.sh
#   SDK_LOCAL=1 ./scripts/run.sh    # verify this suite before anything is published
#
# Two conditions make a run meaningless rather than failing, and each one skips with a reason
# instead:
#
#   1. Nothing published satisfies the range in the csproj. Before the first release there is no
#      artifact, and unlike an interpreted language a C# test naming a method that version does not
#      have will not COMPILE, so this gate covers the whole suite rather than one test.
#   2. The staging key is missing or EMPTY. The suite still runs and each test that needs a key
#      skips from inside it, so the skip and its reason land in the test output rather than only
#      here.
#
# SDK_LOCAL packs the working tree and restores THAT, which is how this suite's own logic is
# verified while nothing is published. It is deliberately not the default and cannot pass
# unnoticed: it prints a banner, and it leaves INTERNETDATA_EXPECTED_VERSION naming a version
# nuget.org does not carry.

set -euo pipefail

cd "$(dirname "$0")/.."

PACKAGE="InternetData"
PROJECT="InternetData.Integration.Tests.csproj"
# The lowercase id is the path the flat container serves under; this is the resolver `dotnet
# restore` itself reads, so the answer is exactly what an install would see.
INDEX="https://api.nuget.org/v3-flatcontainer/internetdata/index.json"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# The suite is built for net8.0, and a box carrying only a newer runtime refuses to launch it at
# all: the run dies as "Zero tests ran", which reads like a broken suite rather than a missing
# runtime. Major rolls onto whatever IS installed, so the floor is used where it exists and a newer
# runtime otherwise. Not Disable, which turns off PATCH roll-forward too and demands an exact
# 8.0.0 no machine carries.
export DOTNET_ROLL_FORWARD=Major

function main() {
    command -v dotnet >/dev/null || {
        echo "==> FAILED: dotnet is not on PATH" >&2
        exit 1
    }
    assertNoProjectReference

    local version="" source=()
    if [ -n "${SDK_LOCAL:-}" ] ; then
        echo "==> LOCAL: building against a package packed from ../src, NOT the published one."
        echo "==> This verifies the suite, and proves nothing about a release."
        version="$(packLocal)"
        source=(--source "$(localFeed)" --source "https://api.nuget.org/v3/index.json")
    else
        local range published
        range="$(declaredRange)"
        published="$(publishedVersions "$range")"
        if [ -z "$published" ] ; then
            skip "no published ${PACKAGE} satisfies ${range}, so there is no released artifact to test"
            return 0
        fi
        echo "==> ${PACKAGE} ${range} matches published ${published//$'\n'/, }"
        version="$(printf '%s\n' "$published" | tail -1)"
    fi

    reportKey

    # Removed so every run resolves afresh. A kept obj/ would pin whatever the first run happened to
    # restore, and the daily run would stop noticing new releases.
    rm -rf obj bin
    echo "==> dotnet restore -p:InternetDataVersion=${version}"
    dotnet restore "$PROJECT" -p:InternetDataVersion="$version" "${source[@]+"${source[@]}"}"
    # The suite asserts its own provenance from the deps.json: that the package was resolved AS a
    # package rather than a project, and that its version is the one selected here.
    INTERNETDATA_EXPECTED_VERSION="$version" \
        dotnet test "$PROJECT" --no-restore -p:InternetDataVersion="$version"
}

# A ProjectReference would make this a second run of the unit suite, silently: every test passes,
# against the wrong code. Asked of MSBuild rather than of the file, so a reference arriving through
# an import or a property is caught too, and asked before anything is built or restored.
function assertNoProjectReference() {
    local items
    items="$(dotnet msbuild "$PROJECT" -getItem:ProjectReference | tr -d ' \n')"
    if [ "$items" != '{"Items":{"ProjectReference":[]}}' ] ; then
        echo "==> FAILED: ${PROJECT} carries a ProjectReference, so this would not test the release:" >&2
        echo "$items" >&2
        exit 1
    fi
}

# The range lives in the csproj, so the gate and the restore read one source of truth.
function declaredRange() {
    sed -n 's|.*<InternetDataVersion[^>]*>\(.*\)</InternetDataVersion>.*|\1|p' "$PROJECT" | head -1
}

# Every stable version the registry will serve that falls inside the range, ascending. A package
# that does not exist answers 404, and one with no matching version answers a list nothing survives;
# both mean the same thing here, so both come back empty.
function publishedVersions() {
    local range="$1" body low high version
    low="${range#[}"
    low="${low%%,*}"
    high="${range##*,}"
    high="${high%)}"
    body="$(curl -fsS "$INDEX" 2>/dev/null || true)"
    # Not a pipeline: a `while` whose last iteration skips a version exits non-zero, which `set -e`
    # would read as the lookup itself having failed.
    while read -r version ; do
        if [ -n "$version" ] && inRange "$version" "$low" "$high" ; then
            echo "$version"
        fi
    # \r as well as \n: nuget.org answers with CRLF, and left in place every version comes out
    # as "\r1.0.0", which the anchored match below drops. The gate would then report nothing
    # published forever, which is indistinguishable from the truth before the first release.
    done < <(printf '%s' "$body" | tr -d ' \r\n' \
        | sed -n 's/.*"versions":\[\([^]]*\)\].*/\1/p' | tr ',' '\n' | tr -d '"' \
        | grep -E '^[0-9]+\.[0-9]+\.[0-9]+$' | sort -V)
    return 0
}

# The half-open range NuGet's own `[low,high)` means: low <= version < high.
function inRange() {
    local version="$1" low="$2" high="$3" lowest highest
    lowest="$(printf '%s\n%s\n' "$version" "$low" | sort -V | head -1)"
    highest="$(printf '%s\n%s\n' "$version" "$high" | sort -V | head -1)"
    [ "$lowest" = "$low" ] && [ "$highest" = "$version" ] && [ "$version" != "$high" ]
}

function localFeed() {
    echo "${TMPDIR:-/tmp}/internetdata-local-feed"
}

# Packs the working tree into a folder feed. The version carries a prerelease suffix so it can never
# be confused with something published, and the feed is wiped first so a stale pack cannot be what
# gets tested.
function packLocal() {
    local feed version
    feed="$(localFeed)"
    version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' \
        ../src/InternetData/InternetData.csproj | head -1)-local"
    rm -rf "$feed"
    mkdir -p "$feed"
    dotnet pack ../src/InternetData/InternetData.csproj -c Release \
        -p:Version="$version" -p:ContinuousIntegrationBuild=false \
        -o "$feed" >/dev/null
    echo "$version"
}

# Names only, never values: these logs are public.
function reportKey() {
    # Empty counts as absent: CI interpolates a secret that does not exist to an empty string, so
    # the variable is SET and a plain unset check never fires, while an empty key is sent as no key
    # at all.
    if [ -n "${INTERNETDATA_STAGING_KEY:-}" ] ; then
        echo "==> INTERNETDATA_STAGING_KEY is set"
    else
        notice "INTERNETDATA_STAGING_KEY is not set: the staging tests skip from inside the suite"
    fi
}

function skip() {
    echo "==> SKIPPED: $1"
    notice "Integration suite skipped: $1"
}

# Surfaced on the workflow run itself, so a skip is visible without opening the log and reading to
# the end of it.
function notice() {
    if [ "${GITHUB_ACTIONS:-}" = "true" ] ; then
        echo "::notice title=Integration::$1"
    fi
}

main "$@"
