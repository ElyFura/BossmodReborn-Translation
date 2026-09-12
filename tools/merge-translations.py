#!/usr/bin/env python3
"""Merge a batch of translations into a language file.

The English text is never typed by hand: it is taken from the assembly dump produced by

    dotnet run --project tools/ShapeCheck -- --missing > missing.json

so the "en" value stored for drift detection always matches what BossMod actually contains.
A batch key that does not appear in the dump is reported and skipped rather than silently added,
which is what catches typos in keys.

Usage:
    python tools/merge-translations.py missing.json batch.txt [--lang de]

Batch format, one per line:
    <key> ||| <translation>
    <key> ||| =              # deliberately stays English
"""

import argparse
import json
import sys
from pathlib import Path

SEP = "|||"


def load_english(path):
    """key -> english, from the ShapeCheck --missing dump.

    JSON rather than a flat table, because BossMod tooltips contain embedded newlines and any
    line-oriented format would have to mangle them - corrupting the very value drift detection compares.
    """
    return json.loads(Path(path).read_text(encoding="utf-8"))


def load_batch(path):
    """key -> translation, from the hand-written batch file."""
    result = {}
    for number, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if SEP not in line:
            print(f"  line {number}: no '{SEP}' separator, skipped", file=sys.stderr)
            continue
        key, translation = (part.strip() for part in line.split(SEP, 1))
        # Hint and UI keys embed the English text, and some of those strings contain real line breaks -
        # CRLF for UI literals, because BossMod's source files are CRLF. Config keys never contain
        # backslashes, so unescaping here is safe for every kind of key.
        key = key.replace("\\r", "\r").replace("\\n", "\n")
        # a key wrapped in double quotes is taken verbatim - needed for the one hint that ends in a
        # space ("Order: "), which stripping the line would otherwise eat
        if len(key) >= 2 and key[0] == '"' and key[-1] == '"':
            key = key[1:-1]
        if key in result:
            print(f"  line {number}: duplicate key {key}", file=sys.stderr)
        if translation == "=":
            # "=" marks a key that deliberately stays English (raid shorthand, direction codes, strat
            # names). Stored as an empty "de" so it counts as decided rather than pending.
            result[key] = ""
        else:
            # the batch file is line-based, so a literal \n is how a translator breaks a long tooltip
            result[key] = translation.replace("\\r", "\r").replace("\\n", "\n")
    return result


def load_existing(path):
    """Existing language file -> (metadata, {key: (de, en)}).

    Every "$"-prefixed key is metadata and is passed through untouched, so adding one ($hintPatterns,
    say) does not require touching this tool.
    """
    if not path.exists():
        return {}, {}
    data = json.loads(path.read_text(encoding="utf-8"))
    meta, entries = {}, {}
    for key, value in data.items():
        if key.startswith("$"):
            meta[key] = value
        elif isinstance(value, str):
            entries[key] = (value, None)
        else:
            entries[key] = (value.get("de"), value.get("en"))
    return meta, entries


def group_of(key):
    """Second path segment, used only to insert blank lines between blocks."""
    parts = key.split("/")
    return parts[1].split("+")[0] if len(parts) > 1 else parts[0]


def render(meta, entries):
    lines = ["{"]
    for key, value in meta.items():
        rendered = json.dumps(value, ensure_ascii=False, indent=2)
        # keep nested metadata readable by re-indenting it one level in
        rendered = rendered.replace(chr(10), chr(10) + "  ")
        lines.append(f"  {json.dumps(key, ensure_ascii=False)}: {rendered},")
        lines.append("")

    ordered = sorted(entries.items())
    previous_group = None
    body = []
    for key, (de, en) in ordered:
        current = group_of(key)
        if previous_group is not None and current != previous_group:
            body.append("")
        previous_group = current
        payload = {"de": de}
        if en is not None:
            payload["en"] = en
        rendered = ", ".join(f"{json.dumps(k, ensure_ascii=False)}: {json.dumps(v, ensure_ascii=False)}"
                             for k, v in payload.items())
        body.append(f"  {json.dumps(key, ensure_ascii=False)}: {{ {rendered} }},")

    # the last real entry must not carry a trailing comma
    for index in range(len(body) - 1, -1, -1):
        if body[index]:
            body[index] = body[index].rstrip(",")
            break

    lines.extend(body)
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("missing", help="ShapeCheck --missing dump (JSON)")
    parser.add_argument("batch", help="batch file with '<key> ||| <translation>' lines")
    parser.add_argument("--lang", default="de")
    args = parser.parse_args()

    target = Path(__file__).resolve().parent.parent / "BmrTranslation" / "Resources" / f"{args.lang}.json"

    english = load_english(args.missing)
    batch = load_batch(args.batch)
    meta, entries = load_existing(target)

    added = updated = skipped = 0
    for key, translation in batch.items():
        if key not in english:
            print(f"  unknown key, not in the assembly dump: {key}", file=sys.stderr)
            skipped += 1
            continue
        if key in entries:
            updated += 1
        else:
            added += 1
        entries[key] = (translation, english[key])

    target.write_text(render(meta, entries), encoding="utf-8")
    print(f"{target.name}: {added} added, {updated} updated, {skipped} skipped, {len(entries)} total")
    return 1 if skipped else 0


if __name__ == "__main__":
    sys.exit(main())
