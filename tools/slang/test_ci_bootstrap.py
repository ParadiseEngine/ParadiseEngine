"""Build the packaged Slang helper outside Git with consumer CI/publish globals."""

import pathlib
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="slang-ci-") as directory:
    temporary = pathlib.Path(directory)
    for name in ("SlangBootstrap.csproj", "SlangBootstrap.cs"):
        shutil.copyfile(root / "tools" / "slang" / name, temporary / name)
    project = ET.Element("Project")
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "SlangBootstrapProject").text = str(temporary / "SlangBootstrap.csproj")
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "SlangShader", Include="unused.slang")
    ET.SubElement(project, "Import", Project=str(root / "src" / "Slang.targets"))
    consumer = temporary / "consumer.proj"
    ET.ElementTree(project).write(consumer, encoding="unicode")
    subprocess.run([
        "dotnet", "msbuild", str(consumer), "-t:_BuildSlangBootstrap",
        "-p:ContinuousIntegrationBuild=true", "-p:DeterministicSourcePaths=true",
        "-p:PublishAot=true", "-p:PublishTrimmed=true", "-p:SelfContained=true",
        "-p:RuntimeIdentifier=linux-x64", "-nologo", "-m:1", "-nr:false",
    ], check=True)
