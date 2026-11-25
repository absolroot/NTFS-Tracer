"""Build and package the Windows release assets in a new directory."""

import argparse
import hashlib
import json
import struct
import subprocess
from pathlib import Path
from tempfile import TemporaryDirectory
from urllib.request import urlopen
from xml.etree import ElementTree
from zipfile import ZIP_DEFLATED, ZipFile


ROOT = Path(__file__).resolve().parents[1]
GUI = ROOT / "gui/NtfsTracer.csproj"
ENGINE = ROOT / "engine/Cargo.toml"
EWF = ROOT / "modules/EvidenceImage/Logical/Native"
BINARIES = ("NtfsTracer.exe", "ntfstracer-cli.exe", "NtfsTracer.libewf.dll")
ARCHITECTURES = {
    "x64": ("x86_64-pc-windows-msvc", 0x8664),
    "x86": ("i686-pc-windows-msvc", 0x014C),
}


def run(*args, capture=False):
    return subprocess.run(args, cwd=ROOT, check=True, text=True, capture_output=capture).stdout


def pe_machine(path):
    with path.open("rb") as stream:
        header = stream.read(64)
        if len(header) == 64 and header[:2] == b"MZ":
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            signature = stream.read(6)
            if len(signature) == 6 and signature[:4] == b"PE\0\0":
                return struct.unpack_from("<H", signature, 4)[0]
    raise ValueError(f"Invalid Windows binary: {path}")


def rust_notices():
    metadata = json.loads(run("cargo", "metadata", "--locked", "--format-version=1", "--manifest-path", ENGINE, capture=True))
    sysroot = Path(run("rustc", "--print", "sysroot", capture=True).strip())
    notices = {
        "Notices/runtime/Rust-Standard-Library-COPYRIGHT.html": sysroot / "share/doc/rust/COPYRIGHT-library.html",
    }
    for crate in metadata["packages"]:
        if crate["source"] is None:
            continue
        name = f"{crate['name']}-{crate['version']}"
        prefix = f"Notices/licenses/rust/{name}"
        directory = Path(crate["manifest_path"]).parent
        for file in directory.iterdir():
            if file.is_file() and file.name.upper().startswith(("LICENSE", "COPYING", "NOTICE", "COPYRIGHT")):
                notices[f"{prefix}/{file.name}"] = file
        if not any(entry.startswith(prefix + "/") for entry in notices) and not (ROOT / "gui" / prefix).is_dir():
            raise FileNotFoundError(f"No license files for {name}")
    return Path(metadata["target_directory"]), notices


def dotnet_notices(packages, arch, version, wpf_notice):
    runtime = packages / f"microsoft.netcore.app.runtime.win-{arch}" / version
    desktop = packages / f"microsoft.windowsdesktop.app.runtime.win-{arch}" / version
    return {
        "Notices/runtime/dotnet-LICENSE.txt": runtime / "LICENSE.TXT",
        "Notices/runtime/dotnet-THIRD-PARTY-NOTICES.txt": runtime / "THIRD-PARTY-NOTICES.TXT",
        "Notices/runtime/windowsdesktop-LICENSE.txt": desktop / "LICENSE",
        "Notices/runtime/WPF-THIRD-PARTY-NOTICES.txt": wpf_notice,
    }


def app_files(publish_dir, machine):
    files = {file.relative_to(publish_dir).as_posix(): file for file in publish_dir.rglob("*") if file.is_file()}
    if {name for name in files if not name.startswith("Notices/")} != set(BINARIES):
        raise ValueError(f"Unexpected files in the Windows publish output: {publish_dir}")
    for name in BINARIES:
        if pe_machine(files[name]) != machine:
            raise ValueError(f"Wrong Windows architecture for {files[name]}")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md", "licenses/Microsoft.Data.Sqlite-MIT.txt",
                 "ewf/COPYING", "ewf/COPYING.LESSER", "ewf/COPYING.zlib", "ewf/README.md"):
        if f"Notices/{name}" not in files:
            raise FileNotFoundError(f"Missing license or notice in {publish_dir}: {name}")
    return files


def write_zip(path, files):
    with ZipFile(path, "w", compression=ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
        for name, file in sorted(files.items()):
            archive.write(file, name)
    with ZipFile(path) as archive:
        if (bad_file := archive.testzip()) is not None:
            raise ValueError(f"Could not verify {bad_file} in {path}")
    print(f"{path.name}: {path.stat().st_size:,} bytes", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="New directory for ZIPs and SHA256SUMS.txt")
    parser.add_argument("--runtime-version", required=True, help="Exact version of the bundled .NET 10 runtime packs")
    options = parser.parse_args()
    output = options.output.resolve()
    if output.exists():
        parser.error(f"Output already exists: {output}")
    version = ElementTree.parse(GUI).findtext("./PropertyGroup/Version")
    release = f"NTFS-Tracer-v{version}"
    runtime_version = options.runtime_version
    if not runtime_version.startswith("10.0.") or not runtime_version.removeprefix("10.0.").isdigit():
        parser.error("Use an exact .NET 10 runtime version such as 10.0.11")

    target_dir, common_notices = rust_notices()
    nuget_output = run("dotnet", "nuget", "locals", "global-packages", "--list", capture=True)
    packages = Path(nuget_output.strip().partition(": ")[2])
    output.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="ntfs-tracer-release-", dir=output.parent) as temporary:
        staging = Path(temporary)
        assets = staging / "assets"
        assets.mkdir()
        wpf_notice = staging / "WPF-THIRD-PARTY-NOTICES.txt"
        wpf_url = f"https://raw.githubusercontent.com/dotnet/wpf/v{runtime_version}/THIRD-PARTY-NOTICES.TXT"
        with urlopen(wpf_url, timeout=30) as response:
            wpf_notice.write_bytes(response.read())

        libewf_sources = list(EWF.glob("libewf_python-*.tar.gz"))
        zlib_sources = list(EWF.glob("zlib-*.tar.gz"))
        if len(libewf_sources) != 1 or len(zlib_sources) != 1:
            raise ValueError("Expected one libewf source archive and one zlib source archive")
        source_files = [EWF / "README.md", EWF / "CMakeLists.txt", *EWF.glob("COPYING*"), *libewf_sources, *zlib_sources]
        write_zip(assets / f"{release}-libewf-source.zip", {file.name: file for file in source_files})

        for arch, (target, machine) in ARCHITECTURES.items():
            run("cargo", "build", "--release", "--locked", "--target", target, "--manifest-path", ENGINE)
            engine = target_dir / target / "release/ntfstracer-cli.exe"
            for profile, suffix in (("Portable", ""), ("SharedRuntime", "-shared-runtime")):
                publish_dir = staging / f"{arch}-{profile}"
                run("dotnet", "publish", GUI, "-c", "Release", "-r", f"win-{arch}", "-o", publish_dir,
                    f"-p:PublishProfile={profile}", f"-p:JournalIndexPath={engine}",
                    f"-p:RuntimeFrameworkVersion={runtime_version}")
                files = app_files(publish_dir, machine)
                files.update(common_notices)
                if profile == "Portable":
                    files.update(dotnet_notices(packages, arch, runtime_version, wpf_notice))
                write_zip(assets / f"{release}-Windows-{arch}{suffix}.zip", files)

        with (assets / "SHA256SUMS.txt").open("w", encoding="ascii", newline="\n") as checksums:
            for archive in sorted(assets.glob("*.zip")):
                with archive.open("rb") as stream:
                    checksums.write(f"{hashlib.file_digest(stream, 'sha256').hexdigest()}  {archive.name}\n")
        assets.rename(output)


if __name__ == "__main__":
    main()
