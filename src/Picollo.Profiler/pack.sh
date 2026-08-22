#!/usr/bin/env bash

set -e

dotnet publish "$(dirname "$0")/Picollo.Profiler.csproj" -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:TrimMode=full
dotnet pack "$(dirname "$0")/Picollo.Profiler.csproj" -c Release --no-build -r linux-x64 -p:PackageVersion=0.0.0 -o "$(dirname "$0")/../../artifacts"
