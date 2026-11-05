#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
export DOTNET_CLI_HOME="$PWD/build/cli-home"
export NUGET_PACKAGES="$PWD/build/packages"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_PROCESSOR_COUNT=2
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export DOTNET_CLI_USE_MSBUILD_SERVER=0
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
dotnet restore ResumableTransferLab.sln --configfile NuGet.Config --disable-parallel -m:1 -nr:false
