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
dotnet build src/ResumableTransferLab.Cli/ResumableTransferLab.Cli.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false >&2
if [ "$#" -eq 0 ]; then
    set -- --both
fi
dotnet build/bin/ResumableTransferLab.Cli/Release/net10.0/ResumableTransferLab.Cli.dll demo "$@"
