#!/usr/bin/env python3
"""Refresh OneArr's offline indexer directory from Prowlarr's public definitions.

Catalog metadata only: this does NOT bundle upstream YAML, install scraper
adapters, fetch credentials or claim all trackers can be searched.
"""
import datetime
import hashlib
import io
import json
import re
import tarfile
import urllib.request
from pathlib import Path

UPSTREAM = "https://codeload.github.com/Prowlarr/Indexers/tar.gz/refs/heads/master"
HERE = Path(__file__).resolve().parents[1]
TARGET = HERE / "web/src/tracker-catalog.json"
PATH = re.compile(r"definitions/v\d+/[^/]+\.yml$")
MAX_ARCHIVE_BYTES = 24 * 1024 * 1024
MAX_DEFINITION_BYTES = 512 * 1024


def field(source: str, name: str) -> str:
    match = re.search(r"^" + re.escape(name) + r":\s*(.*)$", source, re.MULTILINE)
    if not match:
        return ""
    value = match.group(1).strip()
    if value[:1] in {"'", '"'} and value[-1:] == value[:1]:
        value = value[1:-1]
    return value


def entry(path: str, source: str) -> dict:
    caps = source.split("\ncaps:", 1)[-1].split("\nsettings:", 1)[0]
    cats = set(re.findall(r"\bcat:\s*(Movies|TV)(?=[/\s,}])", caps))
    return {
        "path": path,
        "name": field(source, "name") or path.rsplit("/", 1)[-1].removesuffix(".yml"),
        "privacy": field(source, "type") or "unknown",
        "language": field(source, "language") or "unknown",
        "movies": "Movies" in cats,
        "tv": "TV" in cats,
    }


def main() -> None:
    req = urllib.request.Request(UPSTREAM, headers={"User-Agent": "OneArr-Catalog-Generator/1.0"})
    with urllib.request.urlopen(req, timeout=40) as response:
        payload = response.read(MAX_ARCHIVE_BYTES + 1)
    if len(payload) > MAX_ARCHIVE_BYTES:
        raise RuntimeError("Upstream archive exceeds configured size limit")

    entries: list[dict] = []
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        for member in archive:
            if not member.isfile():
                continue
            # Strip archive's top-level directory; never extract paths.
            rel = "/".join(member.name.split("/")[1:])
            if not PATH.fullmatch(rel):
                continue
            if member.size > MAX_DEFINITION_BYTES:
                continue
            handle = archive.extractfile(member)
            if handle is None:
                continue
            data = handle.read(MAX_DEFINITION_BYTES + 1)
            if len(data) > MAX_DEFINITION_BYTES:
                continue
            entries.append(entry(rel, data.decode("utf-8-sig", errors="replace")))

    entries.sort(key=lambda item: item["path"])
    if len(entries) < 500:
        raise RuntimeError(f"Incomplete upstream source ({len(entries)} indexer definitions)")
    checksum = hashlib.sha256(json.dumps(entries, sort_keys=True).encode()).hexdigest()[:16]
    manifest = {
        "source": "Prowlarr/Indexers",
        "sourceMetadataHash": checksum,
        "definitionPaths": [e["path"] for e in entries],
        "entries": entries,
    }
    TARGET.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Updated {TARGET}: {len(entries)} definitions, digest {checksum}")


if __name__ == "__main__":
    main()
