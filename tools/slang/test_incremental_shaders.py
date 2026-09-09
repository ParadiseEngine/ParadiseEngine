"""Verify real shader recompilation when external includes change or switch roots."""

import json
import os
import pathlib
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix="slang-incremental-") as directory:
    temporary = pathlib.Path(directory)
    first = temporary / "first includes"
    second = temporary / "second includes"
    for folder in (first, second):
        (folder / "Common").mkdir(parents=True)
        (folder / "Common" / "layout.slang").write_text(
            "struct ProbeVolume { float4 values[8]; };\n"
        )
    shader = temporary / "consumer.slang"
    shader.write_text(
        '#include "Common/layout.slang"\n'
        "[[vk::binding(9, 3)]] ConstantBuffer<ProbeVolume> volume;\n"
        "[[vk::binding(0, 0)]] RWStructuredBuffer<float4> result;\n"
        '[shader("compute")] [numthreads(1, 1, 1)]\n'
        "void main() { result[0] = volume.values[0]; }\n"
    )
    project = ET.Element("Project")
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "IntermediateOutputPath").text = str(temporary / "obj") + "/"
    ET.SubElement(project, "Import", Project=str(root / "src" / "Slang.targets"))
    # A consumer can add include paths after the toolchain import, without SlangShaderInclude.
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "SlangShader", Include=str(shader))
    ET.SubElement(items, "SlangIncludeDir", Include="$(FixtureIncludeRoot)")
    consumer = temporary / "consumer.proj"
    ET.ElementTree(project).write(consumer, encoding="unicode")
    reflection = temporary / "obj" / "shaders" / "consumer.reflection.json"

    def build(include_root):
        run = subprocess.run([
            "dotnet", "msbuild", str(consumer), "-t:CompileSlangShaders",
            f"-p:FixtureIncludeRoot={include_root}", "-nologo", "-m:1", "-nr:false", "-v:diagnostic",
        ], capture_output=True, text=True)
        if run.returncode:
            raise AssertionError(run.stdout + run.stderr)
        data = json.loads(reflection.read_text())
        volume = next(item for item in data["parameters"] if item["name"] == "volume")
        size = volume["type"]["elementType"]["fields"][0]["binding"]["size"]
        return size, reflection.stat().st_mtime_ns

    size, initial = build(first)
    assert size == 128, size
    assert build(first) == (128, initial), "Unchanged input rebuilt the shader"
    time.sleep(1.1)  # Keep input newer than output even on coarse timestamp filesystems.
    (first / "Common" / "layout.slang").write_text(
        "struct ProbeVolume { float4 values[9]; };\n"
    )
    size, changed = build(first)
    assert size == 144 and changed > initial, "External layout change reused a stale shader"
    assert build(first) == (144, changed), "Updated shader did not become incremental"

    # The alternate root predates the current output: timestamps alone cannot detect this switch.
    old_time = time.time() - 3600
    os.utime(second / "Common" / "layout.slang", (old_time, old_time))
    size, switched = build(second)
    assert size == 128 and switched > changed, "Switching include roots reused a stale shader"
    assert build(second) == (128, switched), "Switched shader did not become incremental"
    print("PASS: include edits and older-root switches rebuild; unchanged builds stay incremental")
