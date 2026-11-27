"""Prepare the exact GPL IV:MP source revision for the Complete Edition port."""

import argparse
import shutil
import subprocess
from pathlib import Path


PORT = Path(__file__).resolve().parents[1]
UPSTREAM = "https://github.com/Neproify/ivmultiplayer.git"
BASE = "e9fadfc74a87f0d1d232f507b2e0bbdf22a1ab52"


def git(root, *args, **kwargs):
    return subprocess.run(["git", "-C", str(root), *args], **kwargs)


def prepare(checkout, source):
    if not (checkout / ".git").is_dir():
        if checkout.exists() and any(checkout.iterdir()):
            raise ValueError(f"Directory is not an empty IV:MP checkout: {checkout}")
        checkout.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "--quiet", "--no-checkout", source, str(checkout)], check=True)
        git(checkout, "checkout", "--quiet", "--detach", BASE, check=True)

    head = git(checkout, "rev-parse", "HEAD", check=True, capture_output=True, text=True).stdout.strip()
    if head != BASE:
        raise ValueError(f"Expected upstream revision {BASE}; found {head}")

    patch = str(PORT / "upstream.patch")
    reapplied = git(checkout, "apply", "--reverse", "--check", patch, capture_output=True).returncode == 0
    if not reapplied:
        git(checkout, "apply", "--check", patch, check=True)

    destination = checkout / "Client/Core/CompleteEdition"
    headers = sorted((PORT / "client").glob("*.h"))
    for header in headers:
        target = destination / header.name
        if target.exists() and target.read_bytes() != header.read_bytes():
            raise ValueError(f"Local changes in {target}. Prepare a fresh checkout.")

    if not reapplied:
        git(checkout, "apply", patch, check=True)
    destination.mkdir(exist_ok=True)
    for header in headers:
        shutil.copy2(header, destination / header.name)
    print(f"Prepared IV:MP {BASE} in {checkout}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path)
    parser.add_argument("--source", default=UPSTREAM, help="An existing local mirror or the upstream URL")
    args = parser.parse_args()
    try:
        prepare(args.checkout.resolve(), args.source)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{error}\n")
