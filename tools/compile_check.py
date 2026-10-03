#!/usr/bin/env python3
"""Offline compile check for Assets/Scripts, without the Unity editor.

Compiles each assembly definition under Assets/Scripts on its own, in dependency order, against
only the assemblies it declares, the way Unity does. A script that reaches across an assembly
boundary fails here exactly as it would in the editor. Two passes:

  editor  every assembly, with UnityEditor and UNITY_EDITOR, as the editor compiles them
  player  runtime assemblies only, without either, as a game build does. Catches editor-only
          code in a runtime assembly, which the editor itself never reports.

It runs the editor's bundled Roslyn against its .NET Standard 2.1 reference assemblies at C# 9,
so it accepts the same C# Unity does. Package assemblies (Unity.InputSystem) come from
Library/ScriptAssemblies, so open the project in Unity once before the first run. Unity's
analyzers and source generators don't run.

    python tools/compile_check.py          # exit 0 = everything compiles

The editor is found from ProjectSettings/ProjectVersion.txt and Unity Hub's install folders.
Override it with UNITY_EDITOR=/path/to/Editor/Data.
"""
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS = os.path.join("Assets", "Scripts")

# What the editor defines and a build doesn't. A script testing any other symbol gets a note,
# because that branch never compiles here.
EDITOR_DEFINES = ["UNITY_EDITOR", "DEBUG", "TRACE", "UNITY_ASSERTIONS"]

# Unity's own compiler options (Library/Bee/artifacts/*/*.rsp): C# 9, and the warnings it hides.
# CS0649 and CS0169 would otherwise fire on every [SerializeField].
OPTIONS = ["-nostdlib+", "-nologo", "-utf8output", "-target:library", "-langversion:9.0",
           "-RuntimeMetadataVersion:v4.0.30319", "-nowarn:0169,0649,0282,1701,1702"]

# asmdef settings this check doesn't model. It refuses them rather than quietly building
# something different from what Unity builds.
UNMODELLED = ("overrideReferences", "precompiledReferences", "defineConstraints",
              "versionDefines", "excludePlatforms")

DECLARATION = re.compile(r"\b(?:class|struct|interface|enum)\s+([A-Za-z_]\w*)")
CONDITION = re.compile(r"^\s*#\s*(?:if|elif)\b(.*)", re.M)
NOT_FOUND = re.compile(r"error CS0246: The type or namespace name '(\w+)'"
                       r"|error CS0103: The name '(\w+)' does not exist")


def fail(message):
    print(f"compile-check: {message}")
    sys.exit(2)


def read_json(path):
    with open(path, encoding="utf-8-sig") as f:   # Unity writes asmdefs with or without a BOM
        return json.load(f)


class Assembly:
    def __init__(self, path):
        data = read_json(path)
        unmodelled = [key for key in UNMODELLED if data.get(key)]
        if data.get("includePlatforms", []) not in ([], ["Editor"]):
            unmodelled.append("includePlatforms")
        if unmodelled:
            fail(f"{path} sets {', '.join(unmodelled)}, which this check doesn't model.")
        self.name = data["name"]
        self.folder = os.path.dirname(path)
        self.refs = data.get("references", [])
        self.editor_only = data.get("includePlatforms") == ["Editor"]
        self.unsafe = data.get("allowUnsafeCode", False)
        self.engine = not data.get("noEngineReferences", False)
        self.sources = []


def find_editor():
    """The Editor/Data folder of the Unity version this project is on."""
    if os.environ.get("UNITY_EDITOR"):
        candidates = [os.environ["UNITY_EDITOR"]]
    else:
        with open(os.path.join("ProjectSettings", "ProjectVersion.txt")) as f:
            version = re.search(r"m_EditorVersion:\s*(\S+)", f.read()).group(1)
        home = os.path.expanduser("~")
        installs = [r"C:\Program Files\Unity\Hub\Editor", os.path.join(home, "Unity", "Hub", "Editor"),
                    "/Applications/Unity/Hub/Editor"]
        # Unity Hub remembers a custom install folder here.
        hubs = [os.path.join(home, ".config", "UnityHub"),
                os.path.join(home, "Library", "Application Support", "UnityHub")]
        if os.environ.get("APPDATA"):
            hubs.insert(0, os.path.join(os.environ["APPDATA"], "UnityHub"))
        for hub in hubs:
            try:
                installs.insert(0, read_json(os.path.join(hub, "secondaryInstallPath.json")))
            except (OSError, ValueError):
                pass
        candidates = [os.path.join(install, version, *data) for install in installs if install
                      for data in (("Editor", "Data"), ("Unity.app", "Contents"))]
    for data in candidates:
        if os.path.isdir(os.path.join(data, "Managed", "UnityEngine")):
            return data
    fail("no Unity editor found. Tried:\n  " + "\n  ".join(candidates)
         + "\nSet UNITY_EDITOR=/path/to/Editor/Data.")


def resolve_guid_references(assemblies):
    """The asmdef inspector writes references as GUID:<the asmdef's guid> by default."""
    if not any(ref.startswith("GUID:") for asm in assemblies.values() for ref in asm.refs):
        return
    names = {}
    for pattern in ("Assets/**/*.asmdef", "Packages/**/*.asmdef", "Library/PackageCache/**/*.asmdef"):
        for path in glob.glob(pattern, recursive=True):
            try:
                with open(path + ".meta") as f:
                    guid = re.search(r"^guid:\s*(\w+)", f.read(), re.M).group(1)
                names[guid] = read_json(path)["name"]
            except (OSError, ValueError, AttributeError, KeyError):
                pass   # left unresolved, it fails below as a missing reference
    for asm in assemblies.values():
        asm.refs = [names.get(ref[len("GUID:"):], ref) if ref.startswith("GUID:") else ref
                    for ref in asm.refs]


def load_assemblies():
    if glob.glob(os.path.join(SCRIPTS, "**", "*.asmref"), recursive=True):
        fail("assembly definition references (.asmref) aren't modelled by this check.")
    assemblies, by_folder = {}, {}
    for path in sorted(glob.glob(os.path.join(SCRIPTS, "**", "*.asmdef"), recursive=True)):
        asm = Assembly(path)
        if asm.name in assemblies or asm.folder in by_folder:
            fail(f"{path} clashes with another assembly definition (same name or same folder).")
        assemblies[asm.name] = by_folder[asm.folder] = asm
    resolve_guid_references(assemblies)

    # A script belongs to the nearest assembly definition above it, as in Unity.
    orphans = []
    for path in sorted(glob.glob(os.path.join(SCRIPTS, "**", "*.cs"), recursive=True)):
        if any(part.endswith("~") for part in path.split(os.sep)):
            continue   # Unity ignores folders ending in ~
        folder = os.path.dirname(path)
        while folder and folder not in by_folder:
            folder = os.path.dirname(folder)
        (by_folder[folder].sources if folder else orphans).append(path)
    if orphans:
        fail("these scripts aren't in any assembly definition:\n  " + "\n  ".join(orphans))
    return assemblies


def dependency_order(assemblies):
    """Assembly names, each after every assembly it references."""
    order, state = [], {}

    def visit(name, chain):
        if state.get(name) == "done":
            return
        if state.get(name) == "visiting":
            fail("assemblies reference each other in a cycle: " + " -> ".join(chain + [name]))
        state[name] = "visiting"
        for ref in assemblies[name].refs:
            if ref in assemblies:
                visit(ref, chain + [name])
        state[name] = "done"
        order.append(name)

    for name in sorted(assemblies):
        visit(name, [])
    return order


def main():
    os.chdir(ROOT)
    sys.stdout.reconfigure(errors="replace")
    data = find_editor()
    dotnet = os.path.join(data, "NetCoreRuntime", "dotnet.exe" if os.name == "nt" else "dotnet")
    cscs = sorted(glob.glob(os.path.join(data, "DotNetSdk", "sdk", "*", "Roslyn", "bincore", "csc.dll")))

    def dlls(*parts):
        return sorted(glob.glob(os.path.join(data, *parts)))

    framework = (dlls("NetStandard", "ref", "2.1.0", "netstandard.dll")
                 + dlls("NetStandard", "compat", "2.1.0", "shims", "*", "*.dll")
                 + dlls("NetStandard", "Extensions", "2.0.0", "*.dll")
                 + dlls("BCLExtensions", "TargetingPacks", "netstandard2.1", "ref", "*.dll"))
    engine = dlls("Managed", "UnityEngine", "UnityEngine*.dll") + dlls("Managed", "UnityEngine", "Unity.Scripting.dll")
    editor = dlls("Managed", "UnityEngine", "UnityEditor*.dll")
    if not (os.path.isfile(dotnet) and cscs and framework and engine and editor):
        fail(f"{data} doesn't have the layout this check expects (NetCoreRuntime, DotNetSdk, NetStandard, Managed).")
    env = dict(os.environ, DOTNET_ROOT=os.path.dirname(dotnet))

    assemblies = load_assemblies()
    order = dependency_order(assemblies)

    # Where each type is declared, to explain "not found" errors that are really boundary crossings.
    homes, notes = {}, set()
    for asm in assemblies.values():
        for path in asm.sources:
            with open(path, encoding="utf-8-sig") as f:
                text = f.read()
            for type_name in DECLARATION.findall(text):
                homes.setdefault(type_name, asm.name)
            for condition in CONDITION.findall(text):
                for symbol in re.findall(r"[A-Za-z_]\w*", condition.split("//")[0]):
                    if symbol not in EDITOR_DEFINES and symbol not in ("defined", "true", "false"):
                        notes.add(f"note: {path} tests {symbol}, which this check never defines.")

    def explain(asm, line):
        found = NOT_FOUND.search(line)
        name = found and (found.group(1) or found.group(2))
        if name in homes and homes[name] != asm.name:
            return line + f"\n      {name} is in {homes[name]}, which {asm.name} doesn't reference."
        return line

    def build(asm, phase, deps, out):
        """(status, reason, compiler output lines)"""
        for dep, future in deps:
            if future.result()[0] != "ok":
                return "skipped", f"needs {dep}", []
        refs = framework + (engine + (editor if phase == "editor" else []) if asm.engine else [])
        for ref in asm.refs:
            if ref in assemblies:
                if phase == "player" and assemblies[ref].editor_only:
                    return "FAILED", f"references {ref}, which is editor-only", []
                refs.append(os.path.join(out, ref + ".dll"))
            elif os.path.isfile(os.path.join("Library", "ScriptAssemblies", ref + ".dll")):
                refs.append(os.path.join("Library", "ScriptAssemblies", ref + ".dll"))
            else:
                return "FAILED", (f"can't find {ref}: not under Assets/Scripts, nor in Library/ScriptAssemblies"
                                  " (open the project in Unity once to build its packages)"), []
        args = OPTIONS + [f'-out:"{os.path.join(out, asm.name + ".dll")}"']
        if phase == "editor":
            args.append("-define:" + ";".join(EDITOR_DEFINES))
        if asm.unsafe:
            args.append("-unsafe+")
        args += [f'-r:"{ref}"' for ref in refs] + [f'"{path}"' for path in asm.sources]
        rsp = os.path.join(out, asm.name + ".rsp")
        with open(rsp, "w", encoding="utf-8") as f:
            f.write("\n".join(args))
        # -noconfig only counts on the command line, not inside a response file.
        result = subprocess.run([dotnet, cscs[-1], "-noconfig", "@" + rsp], env=env, capture_output=True)
        lines = (result.stdout + result.stderr).decode("utf-8", "replace").splitlines()
        lines = [explain(asm, line) for line in lines if line.strip()]
        return ("ok" if result.returncode == 0 else "FAILED"), "", lines

    print(f"compile-check: Unity at {data}")
    tasks = {}
    with tempfile.TemporaryDirectory() as tmp, ThreadPoolExecutor() as pool:
        for phase in ("editor", "player"):
            out = os.path.join(tmp, phase)
            os.makedirs(out)
            # Submitted in dependency order, so a task waiting on its references never starves them.
            for name in order:
                asm = assemblies[name]
                if phase == "player" and asm.editor_only:
                    continue
                deps = [(ref, tasks[phase, ref]) for ref in asm.refs if (phase, ref) in tasks]
                tasks[phase, name] = pool.submit(build, asm, phase, deps, out)
        results = {key: task.result() for key, task in tasks.items()}

    printed, failed = set(), False
    for (phase, name), (status, reason, lines) in results.items():
        # The player pass repeats the editor pass's diagnostics; show each once.
        new = [line for line in lines if line not in printed]
        printed.update(new)
        if status == "FAILED" and lines and not new:
            reason = "same errors as the editor pass"
        count = len(assemblies[name].sources)
        print(f"  {phase:<7} {name:<20} {status:<8} {count} script{'' if count == 1 else 's'}  {reason}".rstrip())
        for line in new:
            print("    " + line)
        if status == "FAILED" and phase == "player" and results["editor", name][0] == "ok":
            print("    It builds in the editor but not in a game: wrap editor-only code in #if UNITY_EDITOR,"
                  " or move it to an editor assembly.")
        failed = failed or status != "ok"
    for note in sorted(notes):
        print(note)
    print("compile-check: FAILED" if failed else "compile-check: OK")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
