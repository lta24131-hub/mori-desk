"""Collect exact license/notice bytes from checksum-pinned resvg lockfile crates.

This is build-time tooling only. Archives remain in an audit cache, never in the
application payload. No archive paths are extracted onto disk.
"""
import argparse
import concurrent.futures
import hashlib
import io
import json
import pathlib
import re
import tarfile
import time
import tomllib
import urllib.request


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fetch(url):
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Nook-notices-build/1.0"})
            with urllib.request.urlopen(request, timeout=40) as response:
                return response.read(48 * 1024 * 1024)
        except Exception:
            if attempt == 2:
                raise
            time.sleep(1 + attempt)


def collect(package, cache, output):
    name, version, checksum = package["name"], package["version"], package["checksum"]
    identity = f"{name}-{version}"
    url = f"https://static.crates.io/crates/{name}/{identity}.crate"
    archive_file = cache / f"{identity}.crate"
    data = archive_file.read_bytes() if archive_file.exists() else fetch(url)
    if sha(data) != checksum:
        raise ValueError(f"Cargo.lock checksum mismatch: {identity}")
    if not archive_file.exists():
        archive_file.write_bytes(data)
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
        members = [member for member in archive.getmembers() if member.isfile()]
        lookup = {member.name: member for member in members}
        cargo = archive.extractfile(lookup[f"{identity}/Cargo.toml"]).read()
        metadata = tomllib.loads(cargo.decode("utf-8"))["package"]
        license_file = metadata.get("license-file")
        notices = []
        for member in sorted(members, key=lambda item: item.name):
            relative = member.name.removeprefix(identity + "/")
            if not member.name.startswith(identity + "/"):
                raise ValueError(f"Unexpected archive member: {member.name}")
            basename = pathlib.PurePosixPath(relative).name
            if not (re.match(r"^(?:licen[cs]e|copying|notice|copyright|authors|unlicense|ofl)(?:[._-]|$)", basename, re.I) or relative == license_file):
                continue
            content = archive.extractfile(member).read()
            if len(content) > 2 * 1024 * 1024:
                raise ValueError(f"Unexpected notice size: {member.name}")
            safe = re.sub(r"[^A-Za-z0-9_.-]", "-", relative)
            target_name = f"notice-{len(notices)+1:02d}-{safe}"
            target = output / "dependencies" / identity / target_name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            notices.append({"sourceMember": member.name, "path": target.relative_to(output).as_posix(), "length": len(content), "sha256": sha(content)})
        vcs_member = lookup.get(f"{identity}/.cargo_vcs_info.json")
        vcs = json.loads(archive.extractfile(vcs_member).read()) if vcs_member else None
        if not notices and name == "cloudabi":
            # This old crate carries its complete BSD text as source headers.
            # Preserve those exact bytes, including both copyright years.
            for source in ("cloudabi.rs", "bitflags.rs"):
                member_name = identity + "/" + source
                content = archive.extractfile(lookup[member_name]).read()
                header = content.split(b"\n\n", 1)[0] + b"\n"
                if b"Redistribution and use in source and binary forms" not in header or b"SUCH DAMAGE." not in header:
                    raise ValueError("CloudABI source license header changed")
                target = output / "dependencies" / identity / (source + "-copyright.txt")
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(header)
                notices.append({"sourceMember": member_name, "sourceRange": "leading comment block, exact bytes", "path": target.relative_to(output).as_posix(), "length": len(header), "sha256": sha(header)})
        if not notices and name in ("phf_codegen", "phf_generator", "phf_shared"):
            commit = vcs["git"]["sha1"]
            if commit != "e235374a7c11721ed7313977c89d5b75ad38c840":
                raise ValueError("Unexpected rust-phf source revision")
            source_url = f"https://raw.githubusercontent.com/sfackler/rust-phf/{commit}/LICENSE"
            content = fetch(source_url)
            if sha(content) != "d8870d5e2ab20fc50915b711c5aa7588131c9abcc1bcb980da1efd7486522a46":
                raise ValueError("Pinned rust-phf license text changed")
            target = output / "dependencies" / identity / "LICENSE.txt"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            notices.append({"sourceUrl": source_url, "path": target.relative_to(output).as_posix(), "length": len(content), "sha256": sha(content)})
        if not notices and name.startswith("winapi-"):
            # The GNU import-library subcrates omit the parent license files.
            # The same lock pins their parent release, including original texts.
            parent_file = cache / "winapi-0.3.9.crate"
            parent_checksum = "5c839a674fcd7a98952e593242ea400abe93992746761e38641405d28b00f419"
            parent = parent_file.read_bytes() if parent_file.exists() else fetch("https://static.crates.io/crates/winapi/winapi-0.3.9.crate")
            if sha(parent) != parent_checksum:
                raise ValueError("Unexpected parent winapi license source")
            with tarfile.open(fileobj=io.BytesIO(parent), mode="r:gz") as parent_archive:
                for source in ("LICENSE-MIT", "LICENSE-APACHE"):
                    member_name = "winapi-0.3.9/" + source
                    content = parent_archive.extractfile(member_name).read()
                    target = output / "dependencies" / identity / (source + ".txt")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(content)
                    notices.append({"sourceArchive": "https://static.crates.io/crates/winapi/winapi-0.3.9.crate", "sourceArchiveSha256": parent_checksum, "sourceMember": member_name, "path": target.relative_to(output).as_posix(), "length": len(content), "sha256": sha(content)})
            source = identity + "/build.rs"
            content = archive.extractfile(lookup[source]).read()
            header = content.split(b"fn main()", 1)[0]
            target = output / "dependencies" / identity / "build-copyright.txt"
            target.write_bytes(header)
            notices.append({"sourceMember": source, "sourceRange": "leading comment block, exact bytes", "path": target.relative_to(output).as_posix(), "length": len(header), "sha256": sha(header)})
        return {"name": name, "version": version, "archiveUrl": url, "archiveSha256": checksum, "license": metadata.get("license"), "licenseFile": license_file, "repository": metadata.get("repository"), "vcs": vcs, "notices": notices}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=pathlib.Path)
    parser.add_argument("--cache", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    lock = args.lock.read_bytes()
    if sha(lock) != "ce2554b92b74fe4a4f16b7f06af35e9739132bf9cb407cde0e22137c86060227":
        raise ValueError("Expected the audited resvg 0.49.0 Cargo.lock bytes")
    packages = [item for item in tomllib.loads(lock.decode("utf-8"))["package"] if item.get("source", "").startswith("registry+")]
    args.cache.mkdir(parents=True, exist_ok=True)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "resvg-Cargo.lock").write_bytes(lock)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        results = list(pool.map(lambda item: collect(item, args.cache, args.output), packages))
    results.sort(key=lambda item: (item["name"], item["version"]))
    inventory = {"schemaVersion": 1, "upstream": "resvg 0.49.0", "lockSource": "https://raw.githubusercontent.com/linebender/resvg/v0.49.0/Cargo.lock", "lockSha256": sha(lock), "scope": "Conservative superset: every registry package in the release Cargo.lock, including build/dev and non-Windows target packages.", "packages": results}
    (args.output / "dependency-notices.json").write_text(json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    missing = [item["name"] + "-" + item["version"] for item in results if not item["notices"]]
    print(json.dumps({"packages": len(results), "noticeFiles": sum(len(item["notices"]) for item in results), "missing": missing, "output": str(args.output)}))
    if missing:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
