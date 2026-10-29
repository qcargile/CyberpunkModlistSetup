import json
import re
from dataclasses import dataclass
from pathlib import Path


MAX_BYTES = 256 * 1024
SETTINGS_NAME = "Chrome & Blood.compiler_settings"
RELEASE_URL = "https://raw.githubusercontent.com/qcargile/Chrome-Blood/main/Modlist/modlists.json"
WABBAJACK_URL = "wabbajack://Chrome_and_Blood/SchoolBoyQ"
COLLECTION_URL = "https://www.nexusmods.com/games/cyberpunk2077/collections/lrcdrr"


@dataclass(frozen=True)
class Notice:
    installed: str
    available: str


def version_parts(value):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]{1,10}(?:\.[0-9]{1,10}){1,3}", value):
        raise ValueError("Invalid release version")
    parts = tuple(int(part) for part in value.split("."))
    if any(part > 2147483647 for part in parts):
        raise ValueError("Invalid release version")
    return parts + (0,) * (4 - len(parts))


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def _constant(value):
    raise ValueError("Invalid JSON constant")


def _json(data):
    if len(data) > MAX_BYTES:
        raise ValueError("Release metadata is too large")
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=_object, parse_constant=_constant)


def installed_version(directory):
    try:
        with (Path(directory) / SETTINGS_NAME).open("rb") as stream:
            settings = _json(stream.read(MAX_BYTES + 1))
        if not isinstance(settings, dict):
            return None
        if settings.get("ModListName") != "Chrome & Blood" or settings.get("MachineUrl") != "Chrome_and_Blood/SchoolBoyQ":
            return None
        version = settings.get("Version")
        version_parts(version)
        return version
    except (OSError, ValueError, RecursionError):
        return None


def update_notice(data, installed):
    try:
        baseline = version_parts(installed)
        releases = _json(data)
        if not isinstance(releases, list):
            return None
        matches = []
        for release in releases:
            if not isinstance(release, dict) or release.get("game") != "cyberpunk2077":
                continue
            links = release.get("links")
            if isinstance(links, dict) and links.get("machineURL") == "SchoolBoyQ":
                matches.append(release)
        if len(matches) != 1:
            return None
        release = matches[0]
        if release.get("force_down") is not False:
            return None
        collection = release["links"].get("nexusCollection")
        if collection is not None:
            if not isinstance(collection, dict):
                return None
            revision = collection.get("lastRevisionNumber")
            if type(revision) is not int or not 0 < revision <= 2147483647:
                return None
            if collection.get("slug") != "lrcdrr" or collection.get("domainName") != "cyberpunk2077":
                return None
        available = release.get("version")
        if version_parts(available) > baseline:
            return Notice(installed, available)
    except (ValueError, RecursionError):
        pass
    return None
