import json
import os
import re
import sys
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


VERSION = "0.2.7"
REPOSITORY_PATTERN = re.compile(r"[A-Za-z0-9-]+/[A-Za-z0-9_.-]+")
VERSION_PATTERN = re.compile(r"v?(\d+)\.(\d+)\.(\d+)")


def repository():
    name = os.environ.get("LIBERTY_FRIENDS_REPO", "").strip()
    if not name:
        locations = [Path(__file__).with_name("release-repo.txt")]
        if getattr(sys, "frozen", False):
            locations.append(Path(sys.executable).with_name("release-repo.txt"))
        for config in locations:
            if config.is_file():
                name = config.read_text(encoding="utf-8").strip()
                break
    return name if REPOSITORY_PATTERN.fullmatch(name) else None


def latest_update(repo):
    request = Request(
        f"https://api.github.com/repos/{repo}/releases/latest",
        headers={"Accept": "application/vnd.github+json", "User-Agent": "LibertyFriends/" + VERSION},
    )
    try:
        with urlopen(request, timeout=5) as response:
            data = json.loads(response.read(1024 * 1024))
    except HTTPError as error:
        if error.code == 404:
            return None
        raise
    tag = data.get("tag_name", "")
    match = VERSION_PATTERN.fullmatch(tag)
    current = tuple(map(int, VERSION.split(".")))
    if match and tuple(map(int, match.groups())) > current:
        return tag
    return None
