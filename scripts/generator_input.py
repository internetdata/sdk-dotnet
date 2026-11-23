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
- The answer's `user_agent` verdict comes only when a lookup sends a
  `user_agent`, which this client does not yet, so it adds no public type
  until it does.

The request's `user_agent` parameter and the batch's `user_agents` stay in:
they reach only the internal WireClient and its internal batch request, whose
body leaves out every member left null.
"""

import sys

# (schema, entry): an entry dropped from a schema's `required` list.
REQUIRED_DROPPED = [
    ("Asn", "country"),
    ("Asn", "rpki_status"),
]

# (schema, property): a property dropped, with everything indented under it.
PROPERTIES_DROPPED = [
    ("LookupResponse", "user_agent"),
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


def main() -> int:
    source, target = sys.argv[1], sys.argv[2]
    with open(source, encoding="utf-8") as f:
        lines = f.readlines()
    for schema, entry in REQUIRED_DROPPED:
        drop_required(lines, schema, entry)
        print(f"  generator input: {schema}.{entry} left optional")
    for schema, prop in PROPERTIES_DROPPED:
        drop_property(lines, schema, prop)
        print(f"  generator input: {schema}.{prop} left out")
    with open(target, "w", encoding="utf-8") as f:
        f.writelines(lines)
    return 0


if __name__ == "__main__":
    sys.exit(main())
