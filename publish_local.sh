#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
output_dir="$root_dir/artifacts/local"
profiler_dir="$root_dir/src/Picollo.Profiler/bin/Release/artifacts"

dotnet publish "$root_dir/src/Picollo/Picollo.csproj" -c Release --no-self-contained -o "$output_dir"
cp "$profiler_dir/win-x64/Picollo.Profiler.dll" "$profiler_dir/win-x64/Picollo.Profiler.pdb" "$output_dir/"
cp "$profiler_dir/linux-x64/Picollo.Profiler.so" "$profiler_dir/linux-x64/Picollo.Profiler.so.dbg" "$output_dir/"
