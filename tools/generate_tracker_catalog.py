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
PROWLARR_NATIVE = "https://codeload.github.com/Prowlarr/Prowlarr/tar.gz/refs/heads/develop"
HERE = Path(__file__).resolve().parents[1]
TARGET = HERE / "web/src/tracker-catalog.json"
PATH = re.compile(r"definitions/v\d+/[^/]+\.yml$")
NATIVE_PATH = re.compile(r"src/NzbDrone\.Core/Indexers/Definitions/[^/]+\.cs$")
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


def read_archive(url: str, maximum: int):
    req = urllib.request.Request(url, headers={"User-Agent": "OneArr-Catalog-Generator/1.0"})
    with urllib.request.urlopen(req, timeout=60) as response:
        payload = response.read(maximum + 1)
    if len(payload) > maximum:
        raise RuntimeError("Upstream source archive is too large")
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        for member in archive:
            if not member.isfile() or member.size > MAX_DEFINITION_BYTES:
                continue
            rel = "/".join(member.name.split("/")[1:])
            if not (PATH.fullmatch(rel) or NATIVE_PATH.fullmatch(rel)):
                continue
            handle = archive.extractfile(member)
            if handle is None:
                continue
            source = handle.read(MAX_DEFINITION_BYTES + 1)
            if len(source) <= MAX_DEFINITION_BYTES:
                yield rel, source.decode("utf-8-sig", errors="replace")


def native_entry(path: str, source: str) -> dict | None:
    # Native C# indexers are catalog entries, NOT executable native adapters in OneArr.
    # Extract only explicit constant metadata; do not evaluate C# or run remote code.
    def match(pattern: str, default: str = "") -> str:
        found = re.search(pattern, source)
        return found.group(1) if found else default

    name = match(r'public\s+override\s+string\s+Name\s*=>\s*"([^"]+)"')
    if not name:
        return None
    privacy = match(r"public\s+override\s+IndexerPrivacy\s+Privacy\s*=>\s*IndexerPrivacy\.(\w+)", "Unknown").lower()
    desc = match(r'public\s+override\s+string\s+Description\s*=>\s*"([^"]*)"')
    url_section = source.split("public override string[] IndexerUrls", 1)[-1].split("public override", 1)[0]
    urls = re.findall(r'"(https://[^"\s]+)"', url_section)[:4]
    # C# settings attributes are used for reference labels only.
    labels = re.findall(r'\[FieldDefinition\([^)]*Label\s*=\s*"([^"]+)"', source, re.DOTALL)
    fields = [{"name": f"field-{i}", "label": label,
               "type": "password" if re.search("password|cookie|token|api.key", label, re.I) else "text"}
              for i, label in enumerate(dict.fromkeys(labels[:30]))]
    return {
        "path": "native/" + path.rsplit("/", 1)[-1],
        "name": name,
        "privacy": privacy,
        "language": "unknown",
        "movies": "MovieSearchParams" in source or "NewznabStandardCategory.Movies" in source,
        "tv": "TvSearchParams" in source or "NewznabStandardCategory.TV" in source,
        "description": desc,
        "links": urls,
        "fields": fields,
        "source": "Prowlarr/Prowlarr",
    }


def main() -> None:
    entries = [entry(path, source) for path, source in read_archive(UPSTREAM, MAX_ARCHIVE_BYTES)
               if PATH.fullmatch(path)]
    if len(entries) < 500:
        raise RuntimeError(f"Incomplete YAML source ({len(entries)} definitions)")
    native = [native_entry(path, source) for path, source in read_archive(PROWLARR_NATIVE, 72 * 1024 * 1024)
              if NATIVE_PATH.fullmatch(path)]
    native = [item for item in native if item is not None]
    if len(native) < 40:
        raise RuntimeError(f"Incomplete Prowlarr native definition source ({len(native)} entries)")
    entries.extend(native)
    entries.sort(key=lambda item: item["path"])
    if len(entries) < 630:
        raise RuntimeError(f"Incomplete combined upstream catalog ({len(entries)} indexer definitions)")
    checksum = hashlib.sha256(json.dumps(entries, sort_keys=True).encode()).hexdigest()[:16]
    manifest = {
        "source": "Prowlarr/Indexers + Prowlarr/Prowlarr",
        "sourceMetadataHash": checksum,
        "definitionPaths": [e["path"] for e in entries],
        "entries": entries,
    }
    TARGET.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Updated {TARGET}: {len(entries)} definitions, digest {checksum}")


if __name__ == "__main__":
    main()
