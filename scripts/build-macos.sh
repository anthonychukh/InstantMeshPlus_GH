#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script must run on macOS." >&2
  exit 2
fi

configuration="${1:-Debug}"
case "$configuration" in
  Debug)
    preset_suffix="debug"
    ;;
  Release)
    preset_suffix="release"
    ;;
  *)
    echo "Configuration must be Debug or Release." >&2
    exit 2
    ;;
esac

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
arm_preset="macos-arm64-${preset_suffix}"
x64_preset="macos-x64-${preset_suffix}"
universal_dir="$repository/artifacts/native/macos-universal/$configuration"

cmake --preset "$arm_preset"
cmake --build --preset "$arm_preset" --target InstantMeshPlus.Worker
cmake --preset "$x64_preset"
cmake --build --preset "$x64_preset" --target InstantMeshPlus.Worker

arm_worker="$repository/artifacts/native/macos-arm64/$configuration/worker/InstantMeshPlus.Worker"
x64_worker="$repository/artifacts/native/macos-x64/$configuration/worker/InstantMeshPlus.Worker"
arm_tbb="$repository/artifacts/native/macos-arm64/$configuration/worker/libtbb.12.dylib"
x64_tbb="$repository/artifacts/native/macos-x64/$configuration/worker/libtbb.12.dylib"

for required_file in "$arm_worker" "$x64_worker" "$arm_tbb" "$x64_tbb"; do
  if [[ ! -f "$required_file" ]]; then
    echo "Missing architecture build output: $required_file" >&2
    exit 1
  fi
done

mkdir -p "$universal_dir"
lipo -create "$arm_worker" "$x64_worker" -output "$universal_dir/InstantMeshPlus.Worker"
lipo -create "$arm_tbb" "$x64_tbb" -output "$universal_dir/libtbb.12.dylib"
chmod 755 "$universal_dir/InstantMeshPlus.Worker" "$universal_dir/libtbb.12.dylib"

install_name_tool -id '@rpath/libtbb.12.dylib' "$universal_dir/libtbb.12.dylib"
if ! otool -L "$universal_dir/InstantMeshPlus.Worker" | grep -q '@rpath/libtbb.12.dylib'; then
  echo 'The universal worker does not reference @rpath/libtbb.12.dylib.' >&2
  otool -L "$universal_dir/InstantMeshPlus.Worker" >&2
  exit 1
fi
if ! otool -l "$universal_dir/InstantMeshPlus.Worker" | grep -q '@loader_path'; then
  echo 'The universal worker does not contain the @loader_path runtime search path.' >&2
  exit 1
fi
if otool -l "$universal_dir/InstantMeshPlus.Worker" | grep -Fq "$repository"; then
  echo 'The universal worker contains a build-machine runtime path.' >&2
  exit 1
fi

for binary in "$universal_dir/InstantMeshPlus.Worker" "$universal_dir/libtbb.12.dylib"; do
  architectures="$(lipo -archs "$binary")"
  [[ "$architectures" == *arm64* && "$architectures" == *x86_64* ]] || {
    echo "$binary is not universal: $architectures" >&2
    exit 1
  }
done

"$universal_dir/InstantMeshPlus.Worker" --help

if [[ "${IMP_ADHOC_SIGN:-0}" == "1" ]]; then
  codesign --force --options runtime --sign - "$universal_dir/libtbb.12.dylib"
  codesign --force --options runtime --sign - "$universal_dir/InstantMeshPlus.Worker"
  codesign --verify --strict --verbose=2 "$universal_dir/libtbb.12.dylib"
  codesign --verify --strict --verbose=2 "$universal_dir/InstantMeshPlus.Worker"
fi

dotnet build "$repository/InstantMeshPlus.sln" -c "$configuration"
dotnet test "$repository/tests/InstantMeshPlus.Core.Tests/InstantMeshPlus.Core.Tests.csproj" \
  -c "$configuration" --no-build
