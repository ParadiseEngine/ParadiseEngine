#!/usr/bin/env python3
"""Selects the projects a change can affect, so pull request CI builds and tests only those.

    select-tests.py              compare HEAD, a pull request's merge commit, with its first parent
    select-tests.py origin/main  compare HEAD with its merge base with origin/main (local preview)
    select-tests.py --full       select everything (pushes to main and release runs)

A changed file touches the project whose directory holds it, and every project that reaches it from
outside its directory: an <Import>, a linked <Compile>/<None> item or a $(MSBuildThisFileDirectory)
path, such as src/Shared/GpuRequirement.cs or src/Slang.targets. Every project that references a
touched one, directly or transitively, is affected. Shared build inputs (global.json, src/Directory.*,
the solution, these scripts) and files no rule maps select everything; documentation selects nothing.

Results go to $GITHUB_OUTPUT (when set) and to the job summary:

    full        true when everything is selected
    build       true when any solution project is affected
    tests       affected test projects (IsTestProject), space-separated
    unaffected  solution projects to drop from the build, space-separated
    coyote      affected Coyote runner directories, as a JSON array
    slang       true when the Slang bootstrap checks (tools/slang/*.py) must run
    aot         true when the managed ECS NativeAOT smoke test must run
"""

import argparse
import fnmatch
import json
import os
import posixpath
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = posixpath.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")).replace("\\", "/")
SOLUTION = "ParadiseEngine.slnx"

# Never part of a build or a test.
IGNORED = [
    "docs/*",
    "AGENTS.md",
    "CLAUDE.md",
    "LICENSE",
    ".cursor/*",
    ".opencara.toml",
    ".gitignore",
    ".gdignore",
    ".github/workflows/codeql.yml",
    ".github/workflows/publish-nuget.yml",
]

# Inputs of every build or of the test lanes themselves, and native tools tests locate at run time
# rather than through MSBuild: a change here runs everything.
FULL = [
    "global.json",
    "nuget.config",
    ".editorconfig",
    ".gitattributes",
    SOLUTION,
    "src/Directory.*",
    ".github/workflows/dotnet-test.yml",
    ".github/scripts/*",
    "third_party/*",
    "tools/ktx/*",
]

# Tests that read repository files directly instead of through project references.
ARCHITECTURE_TEST = "src/Architecture/Paradise.Architecture.Test/Paradise.Architecture.Test.csproj"
READERS = [
    ("README.md", ARCHITECTURE_TEST),
    ("src/*.csproj", ARCHITECTURE_TEST),
]

# Workflow checks outside the solution, keyed by output, with the paths they exercise.
SLANG_INPUTS = ["tools/slang/*", "src/Slang.targets"]
AOT_SMOKE = "tools/ecs-managed-aot/ManagedAotSmoke.csproj"

THIS_FILE_DIRECTORY = "$(MSBuildThisFileDirectory)"
PROJECT_DIRECTORY = "$(MSBuildProjectDirectory)"
ROOTED_PATH = re.compile(r"\$\((?:MSBuildThisFileDirectory|MSBuildProjectDirectory)\)[^'\";\s]*")
UNRESOLVED = re.compile(r"[$@%*?]")


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True).stdout


def matches(path, patterns):
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)


def relative(path):
    path = posixpath.relpath(path, ROOT)
    return None if path == "." or path.startswith("../") else path


class Project:
    """One tracked project file, its project references and the files it reads outside its directory."""

    def __init__(self, path, tracked):
        self.path = path
        self.directory = posixpath.dirname(path)
        self.name = posixpath.splitext(posixpath.basename(path))[0]
        self.references = set()
        # (repository-relative path, is a directory prefix)
        self.inputs = set()
        self._tracked = tracked
        self._scanned = set()
        self._scan(path)
        with open(posixpath.join(ROOT, path), encoding="utf-8-sig") as handle:
            self.is_test = "<IsTestProject>true</IsTestProject>" in handle.read()

    def _scan(self, path):
        if path in self._scanned:
            return
        self._scanned.add(path)
        file_directory = posixpath.dirname(posixpath.join(ROOT, path))
        project_directory = posixpath.join(ROOT, self.directory)
        try:
            tree = ET.parse(posixpath.join(ROOT, path))
        except ET.ParseError as error:
            raise SystemExit(f"cannot parse {path}: {error}")

        for element in tree.iter():
            tag = element.tag.rsplit("}", 1)[-1]
            values = list(element.attrib.values()) + [element.text or ""]
            for value in values:
                for match in ROOTED_PATH.findall(value):
                    self._add_input(match, file_directory, project_directory)

            if tag == "Import":
                imported = self._resolve(element.get("Project", ""), file_directory, file_directory, project_directory)
                if imported and not imported[1] and imported[0] in self._tracked:
                    self.inputs.add(imported)
                    self._scan(imported[0])
            elif "Include" in element.attrib:
                for value in element.get("Include").split(";"):
                    resolved = self._resolve(value, project_directory, file_directory, project_directory)
                    if not resolved:
                        continue
                    if tag == "ProjectReference":
                        self.references.add(resolved[0])
                    else:
                        self._add_input(value, file_directory, project_directory)

    def _add_input(self, value, file_directory, project_directory):
        resolved = self._resolve(value, project_directory, file_directory, project_directory)
        if resolved and not (resolved[0] + "/").startswith(self.directory + "/"):
            self.inputs.add(resolved)

    @staticmethod
    def _resolve(value, base, file_directory, project_directory):
        """A value as a repository path; wildcards and unknown properties leave the directory before them."""
        value = value.strip().replace("\\", "/")
        value = value.replace(THIS_FILE_DIRECTORY, file_directory + "/").replace(PROJECT_DIRECTORY, project_directory)
        prefix = False
        cut = UNRESOLVED.search(value)
        if cut:
            value = value[: value.rfind("/", 0, cut.start()) + 1]
            prefix = True
        if not value:
            return None
        path = relative(posixpath.normpath(posixpath.join(base, value)))
        if path is None:
            return None
        return path, prefix or value.endswith("/")

    def reads(self, changed):
        return any(changed == path or (prefix or not posixpath.splitext(path)[1]) and changed.startswith(path + "/")
                   for path, prefix in self.inputs)


def changed_files(base):
    """Paths the change adds, modifies or deletes, renames as both sides."""
    if base is None:
        parents = git("rev-list", "--parents", "-n", "1", "HEAD").split()
        if len(parents) != 3:
            return None, "HEAD is not a pull request merge commit"
        base = "HEAD^1"
    try:
        output = git("diff", "--name-only", "--no-renames", "-z", f"{base}...HEAD")
    except subprocess.CalledProcessError as error:
        return None, f"cannot diff against {base}: {error.stderr.strip()}"
    return [path for path in output.split("\0") if path], None


def solution_projects():
    with open(posixpath.join(ROOT, SOLUTION), encoding="utf-8-sig") as handle:
        return [path.replace("\\", "/") for path in re.findall(r'Path="([^"]+\.csproj)"', handle.read())]


def select(changes, projects):
    """The touched projects and why, or None with the reason everything runs."""
    by_directory = {}
    for project in projects.values():
        by_directory.setdefault(project.directory, []).append(project.path)

    touched = {}
    for path in changes:
        if matches(path, IGNORED):
            continue
        if matches(path, FULL):
            return None, f"{path} is a shared build or CI input"

        hits = [project for pattern, project in READERS if fnmatch.fnmatchcase(path, pattern)]
        directory = posixpath.dirname(path)
        while directory:
            if directory in by_directory:
                hits.extend(by_directory[directory])
                break
            directory = posixpath.dirname(directory)
        hits.extend(project.path for project in projects.values() if project.reads(path))
        if not hits and matches(path, SLANG_INPUTS):
            continue
        if not hits:
            return None, f"{path} belongs to no project"
        for hit in hits:
            touched.setdefault(hit, path)
    return touched, None


def dependents(touched, projects):
    referenced_by = {}
    for project in projects.values():
        for reference in project.references:
            referenced_by.setdefault(reference, set()).add(project.path)
    affected = set()
    pending = list(touched)
    while pending:
        path = pending.pop()
        if path in affected:
            continue
        affected.add(path)
        pending.extend(referenced_by.get(path, ()))
    return affected


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("base", nargs="?", help="compare with the merge base of this revision instead")
    parser.add_argument("--full", action="store_true", help="select every project")
    args = parser.parse_args()

    tracked = set(git("ls-files", "-z").split("\0"))
    projects = {path: Project(path, tracked) for path in sorted(tracked) if path.endswith(".csproj")}
    solution = solution_projects()

    changes, reason = ([], "full run requested") if args.full else changed_files(args.base)
    touched = None
    if changes is not None and not args.full:
        touched, reason = select(changes, projects)

    full = touched is None
    affected = set(projects) if full else dependents(touched, projects)
    selected = [path for path in solution if path in affected]
    tests = [path for path in selected if projects[path].is_test]
    coyote = [projects[path].directory for path in selected if projects[path].name.endswith(".CoyoteTest")]
    slang = full or any(matches(path, SLANG_INPUTS) for path in changes)
    aot = AOT_SMOKE in affected

    outputs = {
        "full": str(full).lower(),
        "build": str(bool(selected)).lower(),
        "tests": " ".join(tests),
        "unaffected": " ".join(path for path in solution if path not in affected),
        "coyote": json.dumps(coyote),
        "slang": str(slang).lower(),
        "aot": str(aot).lower(),
    }

    lines = ["## Test selection", ""]
    if full:
        lines.append(f"Running everything: {reason}.")
    else:
        lines.append(f"{len(changes)} changed file(s) touch {len(touched)} project(s); "
                     f"{len(selected)} of {len(solution)} solution projects are affected.")
        if touched:
            lines += ["", "| Touched project | First change |", "| --- | --- |"]
            lines += [f"| `{path}` | `{change}` |" for path, change in sorted(touched.items())]
    lines += ["", f"**Test projects ({len(tests)}):** " + (", ".join(f"`{projects[p].name}`" for p in tests) or "none"),
              f"**Coyote runners ({len(coyote)}):** " + (", ".join(f"`{posixpath.basename(d)}`" for d in coyote) or "none"),
              f"**Slang bootstrap checks:** {'yes' if slang else 'no'}",
              f"**Managed ECS NativeAOT smoke:** {'yes' if aot else 'no'}"]
    summary = "\n".join(lines) + "\n"
    print(summary)

    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write(summary)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as handle:
            handle.writelines(f"{key}={value}\n" for key, value in outputs.items())
    else:
        for key, value in outputs.items():
            print(f"{key}={value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
