#!/usr/bin/env python3
"""Restore and build a consumer of every staged library package before anything is pushed.

Usage: verify_staged_packages.py <feed-directory> <version>

Packing proves each package builds from source, where every Paradise dependency is a project
reference. A consumer sees only package references. This builds a throwaway consumer of every
staged library package at exactly <version>, with each staged id resolvable only from the
staging feed and everything else from nuget.org. A Paradise dependency that was not staged, or
that names another version, fails restore here instead of breaking consumers after the push;
resolving it to a different nuget.org release (NU1603) is an error, not a warning.
dotnet tool packages (PackageType DotnetTool) cannot be referenced and are skipped.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from xml.sax.saxutils import quoteattr


def staged_libraries(feed: Path, version: str) -> list[str]:
    ids = []
    for package in sorted(feed.glob(f"*.{version}.nupkg")):
        with zipfile.ZipFile(package) as archive:
            nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec") and "/" not in name)
            root = ET.fromstring(archive.read(nuspec))
        metadata = next(child for child in root if child.tag.endswith("metadata"))
        package_id = next(child.text for child in metadata if child.tag.endswith("}id") or child.tag == "id")
        types = [element.get("name") for element in metadata.iter() if element.tag.endswith("packageType")]
        if "DotnetTool" in types:
            continue
        ids.append(package_id)
    return ids


def write_consumer(work: Path, feed: Path, version: str, ids: list[str]) -> None:
    patterns = "\n".join(f"      <package pattern={quoteattr(package_id)} />" for package_id in ids)
    (work / "nuget.config").write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="staged" value={quoteattr(str(feed))} />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="staged">
{patterns}
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
""", encoding="utf-8")

    references = "\n".join(
        f"    <PackageReference Include={quoteattr(package_id)} Version={quoteattr(f'[{version}]')} />" for package_id in ids)
    (work / "StagedConsumer.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <!-- An unstaged dependency would otherwise resolve to an older nuget.org release with only a
         warning (NU1603): the very drift this check exists to catch. -->
    <WarningsAsErrors>$(WarningsAsErrors);NU1603;NU1605;NU1608</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
{references}
  </ItemGroup>
</Project>
""", encoding="utf-8")
    (work / "Consumer.cs").write_text("namespace StagedConsumer;\n\npublic static class Marker\n{\n}\n", encoding="utf-8")


def run(arguments: list[str], work: Path, environment: dict[str, str]) -> int:
    print("+ " + " ".join(arguments), flush=True)
    return subprocess.run(arguments, cwd=work, env=environment, check=False).returncode


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    feed = Path(sys.argv[1]).resolve()
    version = sys.argv[2]
    ids = staged_libraries(feed, version)
    if not ids:
        print(f"::error::No library packages at version {version} in {feed}")
        return 1
    print(f"Consuming {len(ids)} staged packages at {version}: {' '.join(ids)}", flush=True)

    # Outside the repository, so its Directory.Build/Packages props and global.json stay out of it.
    with tempfile.TemporaryDirectory(prefix="staged-consumer-") as directory:
        work = Path(directory) / "consumer"
        work.mkdir()
        write_consumer(work, feed, version, ids)
        # A private package folder, so a package cached from an earlier run cannot stand in for a
        # staged one. Beside the project, not inside it: the SDK's default **/*.cs glob would
        # compile sources that packages ship as content.
        environment = dict(os.environ, NUGET_PACKAGES=str(Path(directory) / "packages"))
        if run(["dotnet", "restore", "StagedConsumer.csproj"], work, environment) != 0:
            print("::error::The staged packages do not restore as a consumer sees them: a Paradise dependency "
                  "is missing from the staging feed or names another version.")
            return 1
        if run(["dotnet", "build", "StagedConsumer.csproj", "--no-restore", "-c", "Release"], work, environment) != 0:
            print("::error::A consumer of the staged packages does not build.")
            return 1
    print("The staged packages restore and build as a consumer.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
