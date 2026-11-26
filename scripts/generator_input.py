#!/usr/bin/env python3

"""Writes the copy of the pinned spec that NSwag generates from.

spec/openapi.yaml stays the published spec byte for byte, so its diff shows a
spec version. A few of the spec's changes would move a public type, which only
a major may do, so they are taken out of the generator's copy here instead, and
each must be found exactly where it is expected or the run fails:

- Spec 2026.11.21 requires the AS's `country` and `rpki_status`, which every
  answer has served since 2026-11-20. Generated as required they would turn
  `Asn.Country` and `Asn.RpkiStatus` from `string?` into `string`, so the
  public type keeps them optional. The decoder still refuses an answer without
  them: Wire.cs's AnswerMembers names both, and HeldOptional says why.
- Spec 2026.11.26 stops requiring the answer's `is_bot`, `is_carrier`,
  `is_satellite`, `is_ssh`, `is_smtp`, `is_web`, `is_dns_resolver` and
  `is_bittorrent`, and the AS's `type`, which Unlimited does not serve (the
  eight since 2026-11-26 09:36 UTC). Generated as optional they would turn
  eight `bool` properties of the public `LookupResponse` into `bool?` and
  `Asn.Type` from `string` into `string?`, so the copy requires them again and
  they keep their types until a major. The decoder accepts an answer without
  them (Wire.cs's AnswerMembers leaves them out, AbsentAllowed says why): an
  absent flag reads false and an absent `type` reads empty.
- Spec 2026.11.23 gives 23 `first_seen` and `last_seen` members `format:
  date`, which NSwag would generate as `DateOnly?` where they were `string?`.
  They stay strings, handed back as served, until a major moves them.

Nothing is dropped whole: the answer's `user_agent` verdict is generated, and
the batch's `user_agents` reaches only the internal batch request, whose body
leaves out every member left null.
"""

import sys

# (schema, entry): an entry dropped from a schema's `required` list.
REQUIRED_DROPPED = [
    ("Asn", "country"),
    ("Asn", "rpki_status"),
]

# (schema, entry, after): an entry put back into a schema's `required` list, right after the
# entry it followed in the spec that last required it. It fails once the spec requires it
# again, or when `after` is not there once.
REQUIRED_KEPT = [
    ("LookupResponse", "is_bot", "is_relay"),
    ("LookupResponse", "is_carrier", "is_anycast"),
    ("LookupResponse", "is_satellite", "is_carrier"),
    ("LookupResponse", "is_ssh", "is_satellite"),
    ("LookupResponse", "is_smtp", "is_ssh"),
    ("LookupResponse", "is_web", "is_smtp"),
    ("LookupResponse", "is_dns_resolver", "is_web"),
    ("LookupResponse", "is_bittorrent", "is_dns_resolver"),
    ("Asn", "type", "domain"),
]

# (schema, property): a property dropped, with everything indented under it.
PROPERTIES_DROPPED = []

# (schema, property): a property's `format: date` dropped, so it stays a string.
DATE_FORMAT_DROPPED = [
    ("ClassDetail", "last_seen"),
    ("ProxyDetail", "first_seen"),
    ("ProxyDetail", "last_seen"),
    ("PubproxyDetail", "last_seen"),
    ("BotDetail", "first_seen"),
    ("BotDetail", "last_seen"),
    ("SshDetail", "first_seen"),
    ("SshDetail", "last_seen"),
    ("SmtpDetail", "first_seen"),
    ("SmtpDetail", "last_seen"),
    ("WebDetail", "first_seen"),
    ("WebDetail", "last_seen"),
    ("DnsResolverDetail", "first_seen"),
    ("DnsResolverDetail", "last_seen"),
    ("BitTorrentDetail", "first_seen"),
    ("BitTorrentDetail", "last_seen"),
    ("AsnPrefix", "first_seen"),
    ("DnsAddressRecord", "last_seen"),
    ("DnsMxRecord", "last_seen"),
    ("DnsNsRecord", "last_seen"),
    ("DnsCnameRecord", "last_seen"),
    ("DisposableDetail", "first_seen"),
    ("DisposableDetail", "last_seen"),
]

SCHEMA_INDENT = " " * 4
FIELD_INDENT = " " * 6
MEMBER_INDENT = " " * 8


def schema_span(lines, name):
    """The [start, end) line range of components.schemas.<name>."""
    header = f"{SCHEMA_INDENT}{name}:\n"
    starts = [i for i, line in enumerate(lines) if line == header]
    if len(starts) != 1:
        raise SystemExit(f"schema {name}: expected one header, found {len(starts)}")
    start = starts[0]
    end = start + 1
    while end < len(lines) and (lines[end].strip() == "" or lines[end].startswith(SCHEMA_INDENT + " ")):
        end += 1
    return start, end


def field_span(lines, start, end, field, name):
    """The [start, end) line range of a schema's `properties:` or `required:`."""
    header = f"{FIELD_INDENT}{field}:\n"
    found = [i for i in range(start, end) if lines[i] == header]
    if len(found) != 1:
        raise SystemExit(f"schema {name}: expected one `{field}:`, found {len(found)}")
    first = found[0] + 1
    last = first
    while last < end and (lines[last].strip() == "" or lines[last].startswith(FIELD_INDENT + " ")):
        last += 1
    return first, last


def drop_required(lines, schema, entry):
    start, end = schema_span(lines, schema)
    first, last = field_span(lines, start, end, "required", schema)
    wanted = f"{MEMBER_INDENT}- {entry}\n"
    hits = [i for i in range(first, last) if lines[i] == wanted]
    if len(hits) != 1:
        raise SystemExit(f"schema {schema}: `{entry}` is no longer required once; review the transform")
    del lines[hits[0]]


def keep_required(lines, schema, entry, after):
    start, end = schema_span(lines, schema)
    first, last = field_span(lines, start, end, "required", schema)
    if any(lines[i] == f"{MEMBER_INDENT}- {entry}\n" for i in range(first, last)):
        raise SystemExit(f"schema {schema}: `{entry}` is required again; review the transform")
    anchors = [i for i in range(first, last) if lines[i] == f"{MEMBER_INDENT}- {after}\n"]
    if len(anchors) != 1:
        raise SystemExit(f"schema {schema}: `{after}` is not in `required` once; review the transform")
    lines.insert(anchors[0] + 1, f"{MEMBER_INDENT}- {entry}\n")


def drop_property(lines, schema, prop):
    start, end = schema_span(lines, schema)
    first, last = field_span(lines, start, end, "properties", schema)
    wanted = f"{MEMBER_INDENT}{prop}:\n"
    hits = [i for i in range(first, last) if lines[i] == wanted]
    if len(hits) != 1:
        raise SystemExit(f"schema {schema}: property `{prop}` not found once; review the transform")
    stop = hits[0] + 1
    while stop < last and (lines[stop].strip() == "" or lines[stop].startswith(MEMBER_INDENT + " ")):
        stop += 1
    del lines[hits[0]:stop]


def drop_date_format(lines, schema, prop):
    start, end = schema_span(lines, schema)
    first, last = field_span(lines, start, end, "properties", schema)
    wanted = f"{MEMBER_INDENT}{prop}:\n"
    hits = [i for i in range(first, last) if lines[i] == wanted]
    if len(hits) != 1:
        raise SystemExit(f"schema {schema}: property `{prop}` not found once; review the transform")
    stop = hits[0] + 1
    while stop < last and (lines[stop].strip() == "" or lines[stop].startswith(MEMBER_INDENT + " ")):
        stop += 1
    formats = [i for i in range(hits[0] + 1, stop) if lines[i] == f"{MEMBER_INDENT}  format: date\n"]
    if len(formats) != 1:
        raise SystemExit(f"schema {schema}: `{prop}` no longer has `format: date` once; review the transform")
    del lines[formats[0]]


def main() -> int:
    source, target = sys.argv[1], sys.argv[2]
    with open(source, encoding="utf-8") as f:
        lines = f.readlines()
    for schema, entry in REQUIRED_DROPPED:
        drop_required(lines, schema, entry)
        print(f"  generator input: {schema}.{entry} left optional")
    for schema, entry, after in REQUIRED_KEPT:
        keep_required(lines, schema, entry, after)
        print(f"  generator input: {schema}.{entry} kept required")
    for schema, prop in PROPERTIES_DROPPED:
        drop_property(lines, schema, prop)
        print(f"  generator input: {schema}.{prop} left out")
    for schema, prop in DATE_FORMAT_DROPPED:
        drop_date_format(lines, schema, prop)
        print(f"  generator input: {schema}.{prop} left a string")
    with open(target, "w", encoding="utf-8") as f:
        f.writelines(lines)
    return 0


if __name__ == "__main__":
    sys.exit(main())
