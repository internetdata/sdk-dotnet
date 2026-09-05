#!/bin/bash

# Regenerates the wire client from the PINNED spec in spec/openapi.yaml.
#
# The output is committed, like the Go and Java SDKs and unlike the Node one:
# a NuGet package is built from source by CI and by anyone who clones this
# repo, and neither should need a code generator on PATH.
#
# NSwag is installed into a scratch tool path at a pinned version, so the
# machine's global tool list is left alone and two runs a year apart produce
# the same client.

set -euo pipefail

cd "$(dirname "$0")/.."

NSWAG_VERSION="${NSWAG_VERSION:-14.6.1}"
TOOLS="${TOOLS:-/tmp/internetdata-nswag-${NSWAG_VERSION}}"
OUT="src/InternetData/Generated/Api.cs"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if [ ! -x "${TOOLS}/nswag" ] ; then
    echo "==> installing NSwag ${NSWAG_VERSION} into ${TOOLS}"
    dotnet tool install NSwag.ConsoleCore --version "$NSWAG_VERSION" --tool-path "$TOOLS"
fi

echo "==> generating ${OUT}"
mkdir -p "$(dirname "$OUT")"

# The whole published spec is pinned, v1 included, so the diff shows the spec version rather than
# an edited subset of it. Only the v2 operations are surfaced: the generated client is internal,
# and scripts/normalize_generated.py demotes the v1-only DTOs so the public API is v2 alone.
#
# Settings that matter, in the order they bite:
#   generateOptionalPropertiesAsNullable  carries the spec's optionality exactly, so a nullable
#                                         `expires` or `bytes` is null rather than a zero value.
#   clientClassAccessModifier             the wire client is an implementation detail.
#   dateType:System.DateOnly              `updated` is a calendar day, not an instant.
#   arrayType:IReadOnlyList               a response collection a caller cannot mutate.
"${TOOLS}/nswag" openapi2csclient \
    /input:spec/openapi.yaml \
    /output:"$OUT" \
    /namespace:InternetData \
    /className:WireClient \
    /clientClassAccessModifier:internal \
    /generateClientInterfaces:false \
    /exceptionClass:WireException \
    /jsonLibrary:SystemTextJson \
    /generateOptionalPropertiesAsNullable:true \
    /generateNullableReferenceTypes:true \
    /generateDataAnnotations:false \
    /generateJsonMethods:false \
    /operationGenerationMode:SingleClientFromOperationId \
    /dateType:System.DateOnly \
    /arrayType:System.Collections.Generic.IReadOnlyList \
    /arrayInstanceType:System.Collections.Generic.List \
    /arrayBaseType:System.Collections.Generic.List \
    /responseArrayType:System.Collections.Generic.IReadOnlyList \
    /newLineBehavior:LF

echo "==> normalizing names"
python3 scripts/normalize_generated.py "$OUT"

echo "==> done. Review the diff, then commit spec/ and ${OUT} together."
