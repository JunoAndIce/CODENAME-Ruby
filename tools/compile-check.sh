#!/usr/bin/env bash
# Offline compile check for the game scripts, without touching the Unity editor.
# Uses Unity's own bundled Roslyn + NetCoreRuntime, so it speaks the same C# the
# editor does. Exit 0 = everything compiles.
#
#   ./tools/compile-check.sh           # compile-check all Assets/Scripts
#
# Override the editor install with UNITY_EDITOR=/path/to/Editor/Data.
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

U="${UNITY_EDITOR:-$HOME/Unity/Hub/Editor/6000.5.10f1/Editor/Data}"
export DOTNET_ROOT="$U/NetCoreRuntime"
M="$U/MonoBleedingEdge/lib/mono/unityjit-linux"
SDK="$U/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll"

"$U/NetCoreRuntime/dotnet" "$SDK" -t:library -noconfig -nostdlib+ -out:/dev/null \
  -r:"$M/mscorlib.dll" -r:"$M/Facades/netstandard.dll" \
  -r:"$M/Facades/System.Runtime.dll" -r:"$M/System.Core.dll" \
  -r:"$U/Managed/UnityEngine/UnityEngine.CoreModule.dll" \
  -r:"$U/Managed/UnityEngine/UnityEngine.PhysicsModule.dll" \
  -r:"$U/Managed/UnityEngine/UnityEngine.InputLegacyModule.dll" \
  -r:"$PWD/Library/ScriptAssemblies/Unity.InputSystem.dll" \
  $(find Assets/Scripts -name '*.cs' ! -name '*.swp*') 2>&1 |
  grep -v '^Microsoft (R)\|^Copyright'
status=${PIPESTATUS[0]}
if [ "$status" -eq 0 ]; then echo "compile-check: OK"; else echo "compile-check: FAILED ($status)"; fi
exit "$status"