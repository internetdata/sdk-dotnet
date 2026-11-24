#!/usr/bin/env python3

"""Makes NSwag's output idiomatic C#, deterministically.

NSwag has no property-name hook on its command line: NJsonSchema camel-cases
dashes but leaves underscores alone, so a wire name like `dataset_id` becomes
the property `Dataset_id`. That is not a C# name, and most of these DTOs are
public API, so the whole surface would ship reading like the JSON. Everything
below is mechanical, derived from the generated file itself rather than from a
list of names someone has to keep in step with the spec, and every change is
printed.

Run from scripts/generate.sh, right after generating, so regeneration stays
byte-stable.
"""

import re
import sys

# Types that exist only to carry the wire: the client throws them, unwraps
# them, or takes them as a parameter. None of them belongs in the public API,
# and one of them (Error) would be a genuinely bad public name.
#
# The Db* block is the LEGACY v1 surface. The whole published spec is pinned so
# the diff shows a spec version rather than an edited subset of it, but v1 is
# customer-specific and this client targets v2 alone, so its envelopes, its
# error bodies and its file response are hidden rather than published as API
# nobody should call.
INTERNAL_TYPES = [
    "WireException",
    "LookupError",
    "EntitlementError",
    "BatchLookupRequest",
    "BatchLookupResponse",
    "BatchLookupError",
    "Error",
    "Response",
    "Response2",
    "Response3",
    "FileResponse",
    "DbChecksumSuccessResponse",
    "DbChecksumSuccessResponseRc",
    "DbMetadataSuccessResponse",
    "DbMetadataSuccessResponseRc",
    "DbMetadata",
    "Size",
    "DbInvalidApiKeyError",
    "DbInvalidApiKeyErrorRc",
    "DbInvalidFormatError",
    "DbInvalidFormatErrorRc",
    "DbInvalidIdError",
    "DbInvalidIdErrorRc",
    "DbUnauthorizedIdError",
    "DbUnauthorizedIdErrorRc",
    "DbNotFoundError",
    "DbNotFoundErrorRc",
    # The IAM surface and the OAuth request and error bodies. The joined spec carries them, but
    # nothing here wraps IAM and client.Oauth builds its own requests and reads its own errors, so
    # a public class would be API nobody calls that only a major could take back. The three OAuth
    # answers client.Oauth returns stay public.
    "AccountRc",
    "Identity",
    "AccountUser",
    "AccountOrgRef",
    "AccountOrgWrap",
    "AccountOrg",
    "ApikeyList",
    "ApikeyDetail",
    "AccountCreateApikeyRequest",
    "AccountCreatedApikey",
    "AccountRevealedApikey",
    "DeviceAuthorizationRequest",
    "TokenRequest",
    "RevokeRequest",
    "OauthError",
    "Response_type",
    "Code_challenge_method",
    # The answer's user_agent verdict, which scripts/generator_input.py leaves out of LookupResponse
    # until a lookup sends a user_agent; NSwag still emits every schema the spec defines.
    "UserAgentVerdict",
    "UserAgentVerdictVerdict",
]

# The spec's one csvgz/mmdb enum is reached from three places, and an inline
# enum gets a name per place, so the generator emits three copies of it. They
# are folded into the single DatabaseFormat a caller passes to the download and
# checksum methods and reads back off DatabaseVersion.Formats.
TYPE_RENAMES = {
    "Format": "DatabaseFormat",
    "Formats": "DatabaseFormat",
    "Response2Format": "DatabaseFormat",
    "DbChecksums": "DatabaseChecksums",
}

# NSwag 14.6 tags enum members with JsonStringEnumMemberName, which only exists from .NET 9, while
# its OWN query-string serializer reads EnumMemberAttribute. Left alone the client does not compile
# on net8.0 and, where it does compile, sends `?format=1` instead of `?format=mmdb`.
#
# Only the QUERY direction needs this. Reads are covered by the per-property
# JsonConverter(JsonStringEnumConverter<T>) NSwag also emits, which matches the C# member name
# case-insensitively.
ENUM_MEMBER = re.compile(
    r'(?P<indent>[ \t]*)\[System\.Text\.Json\.Serialization\.JsonStringEnumMemberName\(@"(?P<wire>[^"]*)"\)\]\n'
    r"(?P<next>[ \t]*)(?P<member>[A-Za-z_][A-Za-z0-9_]*) = "
)

ENUM_BLOCK = re.compile(
    r"\n[ \t]*\[System\.CodeDom[^\n]*\]\n[ \t]*public enum DatabaseFormat\n[ \t]*\{.*?\n[ \t]*\}\n",
    re.DOTALL,
)

PROPERTY = re.compile(
    r'(\[System\.Text\.Json\.Serialization\.JsonPropertyName\("(?P<wire>[^"]+)"\)\]\n'
    # An enum property carries a second attribute between the two, and skipping it would leave
    # Download.Format named after its own type. A LIST of enums carries a comment there instead of
    # an attribute, and skipping that would leave DatabaseVersion.Formats named DatabaseFormat.
    r"(?:[ \t]*(?:\[|//)[^\n]*\n)*"
    r"\s*public\s+[^\n]*?\s)(?P<name>[A-Za-z_][A-Za-z0-9_]*)(?=\s*\{ get; set; \})"
)

# An AS number is 32 bits UNSIGNED, and the spec types every one as a bare `integer`, which NSwag
# writes as a 32-bit int: GET /asn/4200000000 answers `"asn": 4200000000`, a private AS (measured on
# prod 2026-11-13), which an int cannot hold, so the whole answer failed to decode. Every member
# carrying one is a long, what `format: int64` generates, so the spec taking that fix changes no
# public type.
AS_NUMBER = re.compile(
    r'(\[System\.Text\.Json\.Serialization\.JsonPropertyName\("(?:asn|other_origins)"\)\]\n'
    r"\s*public\s+(?:System\.Collections\.Generic\.IReadOnlyList<)?)int(\??>?\s+\w+\s*\{ get; set; \})"
    r"( = new System\.Collections\.Generic\.List<)?(?(3)int>\(\);|)"
)

# NSwag writes a per-property converter for a scalar enum and this comment for an enum inside a
# LIST, where System.Text.Json's default is to read a NUMBER. Every enum an answer carries is
# opened below and carries its own converter, so the comment goes with it, and one left behind
# names a list nothing reads.
ITEM_CONVERTER_TODO = re.compile(
    r"[ \t]*// TODO\(system\.text\.json\): Add string enum item converter\n"
    r"(?=[ \t]*public [^\n]*?IReadOnlyList<(?P<item>[A-Za-z_][A-Za-z0-9_]*)>)"
)

# A C# enum cannot hold a value it was not built with, so a value the API adds to one an answer
# carries failed the whole call in every installed release (5.15.0, measured 2026-11-17 on a tier,
# a standing, a license type, a format and an outcome). Each such enum becomes a class holding the
# value as served, with a property per value the spec lists, compared by value, the shape of
# System.Net.Http.HttpMethod. An enum only a request takes (OAuth's) stays an enum.
ENUM_DECLARATION = re.compile(
    r"(?P<attr>[ \t]*\[System\.CodeDom\.Compiler\.GeneratedCode\([^\n]*\)\]\n)"
    r"[ \t]*public enum (?P<name>[A-Za-z_][A-Za-z0-9_]*)\n[ \t]*\{\n(?P<body>.*?)\n[ \t]*\}\n",
    re.DOTALL,
)

OPEN_MEMBER = re.compile(
    r'\[System\.Runtime\.Serialization\.EnumMember\(Value = @"(?P<wire>[^"]*)"\)\]\n'
    r"[ \t]*(?P<member>[A-Za-z_][A-Za-z0-9_]*) = [0-9]+,"
)

OPEN_CLASS = """{attr}    [System.Text.Json.Serialization.JsonConverter(typeof(VocabularyConverter<{name}>))]
    public sealed class {name} : System.IEquatable<{name}>, IVocabulary<{name}>
    {{
{members}
        /// <summary>Every value this release was built with, in the spec's order.</summary>
        public static System.Collections.Generic.IReadOnlyList<{name}> Known {{ get; }} = [{known}];

        /// <summary>A value as the API spells it, one this release was built with or not.</summary>
        public {name}(string value)
        {{
            Value = value ?? throw new System.ArgumentNullException(nameof(value));
        }}

        /// <summary>The value as the API spells it.</summary>
        public string Value {{ get; }}

        /// <summary>
        /// Whether this release was built with the value, one of <see cref="Known"/>. A value the API
        /// added since reads as itself with this false, rather than failing the call.
        /// </summary>
        public bool IsKnown => System.Linq.Enumerable.Contains(Known, this);

        public bool Equals({name}? other) => other is not null && string.Equals(Value, other.Value, System.StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as {name});

        public override int GetHashCode() => System.StringComparer.Ordinal.GetHashCode(Value);

        /// <summary>The value as the API spells it.</summary>
        public override string ToString() => Value;

        public static bool operator ==({name}? left, {name}? right) => left is null ? right is null : left.Equals(right);

        public static bool operator !=({name}? left, {name}? right) => !(left == right);

        static {name} IVocabulary<{name}>.Of(string value) => new(value);
    }}
"""


# NSwag copies each response's headers through HttpHeaders' validating enumerator, which parses
# every header .NET knows and hands back the parsed value re-formatted. Retry-After goes through
# .NET's own HTTP date reader, which takes an offset, `UTC`, no zone, a two-digit year and
# one-digit fields, and comes back as an IMF-fixdate in GMT, so Wire's strict read never saw what
# the server sent: through 2.7.1 a 429 dated `+0500` was waited out rather than read as a spent
# quota (measured 2026-11-11). NonValidated hands each header back as it arrived.
RESPONSE_HEADERS = re.compile(r"foreach \(var item_ in response_\.Headers\)\n")


def main() -> int:
    path = sys.argv[1]
    with open(path, encoding="utf-8") as f:
        src = f.read()

    # Types first: renaming a type also hits a property of that name, and the
    # property pass then puts it back from the wire name it carries.
    src, retyped = rename_types(src)
    src, folded = fold_duplicate_format_enums(src)
    src, renamed = pascal_case_properties(src)
    src, hidden = hide_wire_types(src)
    src, members = fix_enum_member_names(src)
    src, opened = open_answer_enums(src)
    src, widened = AS_NUMBER.subn(lambda m: m.group(1) + "long" + m.group(2) + (m.group(3) + "long>();" if m.group(3) else ""), src)
    src, raw = RESPONSE_HEADERS.subn("foreach (var item_ in response_.Headers.NonValidated)\n", src)

    with open(path, "w", encoding="utf-8") as f:
        f.write(src)

    for old, new in renamed:
        print(f"  property {old} -> {new}")
    for old, new in retyped:
        print(f"  type     {old} -> {new}")
    for name in hidden:
        print(f"  internal {name}")
    for name in opened:
        print(f"  open     {name}")
    print(f"  {len(renamed)} properties, {len(retyped)} types renamed, "
          f"{folded} duplicate enums folded, {len(hidden)} hidden, {members} enum members retagged, "
          f"{len(opened)} answer enums opened, {raw} header copies left unparsed, {widened} AS numbers widened")

    return check(src)


def pascal_case_properties(src):
    """Renames every property to the PascalCase of the wire name above it.

    Derived from the JsonPropertyName attribute rather than from the C# name,
    so the result is the name the API actually serves and a generator that
    starts mangling names differently cannot quietly satisfy this.
    """
    renamed = []

    classes = class_starts(src)

    def fix(m):
        want = wanted_name(m, classes)
        have = m.group("name")
        if want == have:
            return m.group(0)
        renamed.append((have, want))
        return m.group(1) + want

    return PROPERTY.sub(fix, src), renamed


# C# forbids a member named like the type that holds it (CS0542), so the one wire name the PascalCase
# rule would turn into its own type's name takes the name here: asn.asn, the AS number.
SELF_NAMED = {("Asn", "asn"): "Number"}


def class_starts(src):
    """Where each generated class begins, by name. The DTOs are never nested, so the last class
    declared before a property is the one holding it."""
    return [(m.start(), m.group(1)) for m in re.finditer(r"^\s*(?:public|internal) partial class (\w+)", src, re.M)]


def wanted_name(m, classes):
    """The name a property should carry: the PascalCase of its wire name, unless that is its own
    type's name."""
    owner = next((name for start, name in reversed(classes) if start < m.start()), None)
    return SELF_NAMED.get((owner, m.group("wire")), pascal_of(m.group("wire")))


def pascal_of(wire):
    """The PascalCase of a wire name, without a vendor namespace.

    `mslm:apikey_id` is `ApikeyId`, as every SDK surfaces it: NSwag keeps the colon, and
    `Mslm:apikeyId` is not an identifier at all.
    """
    return "".join(part[:1].upper() + part[1:] for part in wire.split(":")[-1].split("_"))


def rename_types(src):
    retyped = []
    # Doc comments are the spec's own prose, where these words are English rather
    # than identifiers, so they are left exactly as the API describes itself.
    lines = src.split("\n")
    for old, new in TYPE_RENAMES.items():
        hit = False
        for i, line in enumerate(lines):
            if line.lstrip().startswith("///"):
                continue
            lines[i], n = re.subn(rf"(?<![\w.]){re.escape(old)}(?![\w])", new, line)
            hit = hit or n > 0
        if hit:
            retyped.append((old, new))
    return "\n".join(lines), retyped


def fold_duplicate_format_enums(src):
    """Keeps one DatabaseFormat declaration out of the renamed copies.

    They must be textually identical: the same spec enum reached three ways
    cannot legitimately differ, so a difference means the spec grew a variant
    this fold would silently discard.
    """
    blocks = ENUM_BLOCK.findall(src)
    if len(blocks) < 2:
        return src, 0
    if len(set(blocks)) != 1:
        raise SystemExit("DatabaseFormat copies differ; the spec's format enums are no longer the same")
    seen = 0

    def keep_first(m):
        nonlocal seen
        seen += 1
        return m.group(0) if seen == 1 else "\n"

    return ENUM_BLOCK.sub(keep_first, src), len(blocks) - 1


def fix_enum_member_names(src):
    """Retags enum members with the attribute NSwag's own query serializer reads.

    Also asserts that each wire value differs from its C# member name only by CASE, which is what
    the READ side needs: NSwag's per-property converter parses with ignoreCase, so `csvgz` finds
    `Csvgz` and the all-caps `SUCCESS` on the v1 envelopes finds `SUCCESS`. A multi-word value like
    `not_found` against a `NotFound` member would NOT be found, and would break reads silently, so
    it fails here loudly instead.
    """
    mismatched = []

    def retag(m):
        wire, member = m.group("wire"), m.group("member")
        if wire.lower() != member.lower():
            mismatched.append(f"{member} serializes as {wire!r}, which no case-insensitive parse finds")
        return (f'{m.group("indent")}[System.Runtime.Serialization.EnumMember(Value = @"{wire}")]\n'
                f'{m.group("next")}{member} = ')

    src, n = ENUM_MEMBER.subn(retag, src)
    if mismatched:
        raise SystemExit("enum wire values no longer match their member names:\n  " + "\n  ".join(mismatched))
    return src, n


def open_answer_enums(src):
    """Turns every enum a DTO property carries into a class that holds any value as served.

    Derived from the file: an enum is opened when a property, or a list a property holds, is of
    its type, so an enum the spec adds to an answer is opened without a list to keep in step. Its
    per-property JsonStringEnumConverter goes too, since the class carries its own converter, which
    a property's attribute would override.
    """
    opened = []

    def carried(name):
        return re.search(
            rf"^[ \t]*public (?:System\.Collections\.Generic\.IReadOnlyList<)?{re.escape(name)}>?\??"
            rf" [A-Za-z_][A-Za-z0-9_]* \{{ get; set; \}}", src, re.M)

    def open_one(m):
        name = m.group("name")
        if not carried(name):
            return m.group(0)
        values = OPEN_MEMBER.findall(m.group("body"))
        if not values:
            raise SystemExit(f"enum {name} has no EnumMember values to open")
        opened.append(name)
        members = "".join(
            f'        /// <summary><c>{wire}</c></summary>\n'
            f'        public static {name} {member} {{ get; }} = new("{wire}");\n\n'
            for wire, member in values)
        members = members[:-1]
        known = ", ".join(member for _, member in values)
        return OPEN_CLASS.format(attr=m.group("attr"), name=name, members=members, known=known)

    src = ENUM_DECLARATION.sub(open_one, src)
    for name in opened:
        src = re.sub(
            rf"[ \t]*\[System\.Text\.Json\.Serialization\.JsonConverter\(typeof\("
            rf"System\.Text\.Json\.Serialization\.JsonStringEnumConverter<{re.escape(name)}>\)\)\]\n", "", src)
    src = ITEM_CONVERTER_TODO.sub(lambda m: "" if m.group("item") in opened else m.group(0), src)
    return src, opened


def hide_wire_types(src):
    """Demotes the wire-only types to internal.

    NSwag applies one access modifier to every DTO it emits, so the envelopes,
    the error bodies and the whole legacy v1 surface come out public alongside
    the models a caller genuinely reads.
    """
    hidden = []
    for name in INTERNAL_TYPES:
        pattern = rf"^(\s*)public (partial class|enum) {re.escape(name)}(?=[\s<:])"
        src, n = re.subn(pattern, r"\1internal \2 " + name, src, flags=re.MULTILINE)
        if n:
            hidden.append(name)
    return src, hidden


def check(src) -> int:
    """Refuses to leave behind the defects this script exists to remove."""
    bad = []
    classes = class_starts(src)
    for m in PROPERTY.finditer(src):
        want = wanted_name(m, classes)
        if m.group("name") != want:
            bad.append(f"property {m.group('name')} should be {want}, from the wire name")
    for name in INTERNAL_TYPES:
        if re.search(rf"^\s*public (?:partial class|enum) {re.escape(name)}(?=[\s<:])", src, re.M):
            bad.append(f"wire type {name} is still public")
    for m in re.finditer(r"^\s*public (?:partial class|enum) (Format\d?|Formats)\b", src, re.M):
        bad.append(f"duplicate format enum {m.group(1)} survived the fold")
    for m in ITEM_CONVERTER_TODO.finditer(src):
        bad.append(f"a list of {m.group('item')} has no item converter")
    public_enums = set(re.findall(r"^[ \t]*public enum ([A-Za-z_][A-Za-z0-9_]*)", src, re.M))
    for m in re.finditer(r"JsonStringEnumConverter<([A-Za-z_][A-Za-z0-9_]*)>", src):
        if m.group(1) not in public_enums:
            continue
        bad.append(f"{m.group(1)} is still a closed enum in an answer, which fails on a value the API adds")
    if AS_NUMBER.search(src):
        bad.append("an AS number is still a 32-bit int")
    if RESPONSE_HEADERS.search(src):
        bad.append("a response's headers are copied through the validating enumerator")
    for line in bad:
        print(f"NORMALIZE FAILED: {line}", file=sys.stderr)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
